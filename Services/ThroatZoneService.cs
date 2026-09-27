using System.Collections.Generic;
using Game.Net;
using Game.Pathfind;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using RailCapacityGuard.Utils;
using NetTrackLane = Game.Net.TrackLane;
using NetLane = Game.Net.Lane;

namespace RailCapacityGuard.Services
{
    /// <summary>
    /// P3：咽喉区分组（自写；思路参考 TTE/RPF/TT 的"读数据不拦函数"）。
    ///
    /// 种子：TrackLaneFlags ∈ { Switch=4, DiamondCrossing=8, Exclusive=16,
    ///        CrossingTraffic=128, MergingTraffic=256, DoubleSwitch=16384 }（dump:20629-20641 已核实）
    /// 分组：用**共享节点**判等 —— Lane.m_StartNode/m_MiddleNode/m_EndNode 是 Game.Pathfind.PathNode
    ///        （dump:18623-18625）。注意：**PathNode.m_SearchKey 不可访问**（编译期 CS0122 证实为
    ///        non-public），因此这里只用公开面：GetHashCode() 分桶 + EqualsIgnoreCurvePos() 判等
    ///        （dump:25887/25885 均为 public）。
    ///        规则：① 两个种子共享任一节点 → 同区组；
    ///              ② 一条非种子车道若同时挨着两个种子 → 把这两个种子并起来；
    ///              ③ 与种子相邻的非种子车道成为区组成员（供 IsBusy 覆盖整个咽喉）。
    /// 查询：IsBusy 只遍历本区组成员（上界 MaxZoneLanes），读 LaneReservation.m_Blocker。
    /// 明确不用 m_Parallelism（归档：换算 ×128 已定，语义标签存疑）。区组以 int id 表示。
    /// </summary>
    public sealed class ThroatZoneService : RailGuardServiceBase
    {
        private const int MaxTrackLanes = 4000;
        // 2026-09-27 实测：1938 条轨道车道时种子已顶满 512 上限被截断（日志 seeds=512），分组不完整且
        // 重建间 zone 数漂移（125↔126）→ 上限提到 2048，覆盖全部种子。
        private const int MaxSeeds = 2048;
        private const int MaxZoneLanes = 64;
        private const int MaxNodeBuckets = 8192;

        private const Game.Net.TrackLaneFlags SeedFlags =
            Game.Net.TrackLaneFlags.Switch
            | Game.Net.TrackLaneFlags.DiamondCrossing
            | Game.Net.TrackLaneFlags.Exclusive
            | Game.Net.TrackLaneFlags.CrossingTraffic
            | Game.Net.TrackLaneFlags.MergingTraffic
            | Game.Net.TrackLaneFlags.DoubleSwitch;

        private struct NodeEntry
        {
            public PathNode Node;
            public int SeedIndex;
        }

        private readonly Dictionary<Entity, int> m_LaneToZone = new Dictionary<Entity, int>();
        private readonly List<List<Entity>> m_Zones = new List<List<Entity>>();
        private readonly HashSet<int> m_BusyLogged = new HashSet<int>();

        // 阶段 3（2026-09-27）：共享段索引——lane 被多条线路的 PathTargets（m_StartLane/m_EndLane，
        // 宿主 Owner.m_Owner==line，dump:6620/49840）引用 ⇒ 共享。只存"≥2 条线"的 lane。
        private readonly Dictionary<Entity, Entity> m_LaneFirstLine = new Dictionary<Entity, Entity>(512);
        private readonly HashSet<Entity> m_SharedLanes = new HashSet<Entity>();

        public int SharedLaneCount => m_SharedLanes.Count;

        public bool IsSharedLane(Entity lane)
        {
            return lane != Entity.Null && m_SharedLanes.Contains(lane);
        }

        /// <summary>重建共享段索引（每 4096 帧；防御外壳同 Rebuild）。</summary>
        public void RebuildSharedLanes(EntityManager entityManager)
        {
            m_LaneFirstLine.Clear();
            m_SharedLanes.Clear();
            try
            {
                EntityQuery query = entityManager.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<Game.Routes.PathTargets>() });
                NativeArray<Entity> hosts = query.ToEntityArray(Allocator.Temp);
                try
                {
                    int n = hosts.Length > 512 ? 512 : hosts.Length;
                    for (int i = 0; i < n; i++)
                    {
                        Entity host = hosts[i];
                        if (!entityManager.HasComponent<Game.Common.Owner>(host))
                        {
                            continue;
                        }

                        Entity owner = entityManager.GetComponentData<Game.Common.Owner>(host).m_Owner;
                        if (owner == Entity.Null)
                        {
                            continue;
                        }

                        Game.Routes.PathTargets targets = entityManager.GetComponentData<Game.Routes.PathTargets>(host);
                        MarkShared(targets.m_StartLane, owner);
                        MarkShared(targets.m_EndLane, owner);
                    }

                    if (m_SharedLanes.Count > 0)
                    {
                        ModLog.Verbose("[P3] shared lanes=" + m_SharedLanes.Count + " hosts=" + n);
                    }
                }
                finally
                {
                    hosts.Dispose();
                }
            }
            catch (System.Exception ex)
            {
                ModLog.Error("[P3] RebuildSharedLanes failed: " + ex);
                m_SharedLanes.Clear();
            }
        }

        private void MarkShared(Entity lane, Entity owner)
        {
            if (lane == Entity.Null || m_SharedLanes.Count >= 1024)
            {
                return;
            }

            Entity first;
            if (m_LaneFirstLine.TryGetValue(lane, out first))
            {
                if (first != owner)
                {
                    m_SharedLanes.Add(lane);
                }
            }
            else if (m_LaneFirstLine.Count < 2048)
            {
                m_LaneFirstLine[lane] = owner;
            }
        }

        public ThroatZoneService(World world) : base(world)
        {
        }

        public override string Name => "P3.ThroatZone";

        public int ZoneCount => m_Zones.Count;
        public int LastSeedCount { get; private set; }
        public uint LastRebuildFrame { get; private set; }

        /// <summary>
        /// 防御外壳：Rebuild 内部任何异常都不得冒泡到 OnUpdate（否则会中断整个
        /// GameSimulation 的系统更新队列）。异常时记日志并清空索引，下次重建自愈。
        /// </summary>
        public void Rebuild(EntityManager entityManager, uint frame)
        {
            try
            {
                RebuildInternal(entityManager, frame);
            }
            catch (System.Exception ex)
            {
                ModLog.Error("[P3] Rebuild failed, zones cleared: " + ex);
                m_Zones.Clear();
                m_LaneToZone.Clear();
                LastSeedCount = 0;
            }
        }

        private void RebuildInternal(EntityManager entityManager, uint frame)
        {
            m_LaneToZone.Clear();
            m_Zones.Clear();
            LastRebuildFrame = frame;
            LastSeedCount = 0;

            EntityQuery query = entityManager.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<NetTrackLane>() });
            NativeArray<Entity> lanes = query.ToEntityArray(Allocator.Temp);
            try
            {
                int n = lanes.Length > MaxTrackLanes ? MaxTrackLanes : lanes.Length;

                List<Entity> seeds = new List<Entity>(MaxSeeds);
                Dictionary<Entity, int> seedIndex = new Dictionary<Entity, int>(MaxSeeds);
                for (int i = 0; i < n && seeds.Count < MaxSeeds; i++)
                {
                    Entity lane = lanes[i];
                    NetTrackLane track = entityManager.GetComponentData<NetTrackLane>(lane);
                    if ((track.m_Flags & SeedFlags) == 0)
                    {
                        continue;
                    }

                    seedIndex[lane] = seeds.Count;
                    seeds.Add(lane);
                }

                LastSeedCount = seeds.Count;
                if (seeds.Count == 0)
                {
                    return;
                }

                Dictionary<int, List<NodeEntry>> nodeToSeeds = new Dictionary<int, List<NodeEntry>>(MaxNodeBuckets);
                for (int i = 0; i < seeds.Count; i++)
                {
                    AddSeedBuckets(entityManager, nodeToSeeds, seeds[i], i);
                }

                int[] parent = new int[seeds.Count];
                for (int i = 0; i < parent.Length; i++)
                {
                    parent[i] = i;
                }

                // ① 种子之间直接共享节点
                for (int i = 0; i < seeds.Count; i++)
                {
                    UnionSeedNodes(entityManager, nodeToSeeds, parent, seeds[i]);
                }

                // ② 非种子车道：若它同时挨着多个种子，把这些种子并起来
                for (int i = 0; i < n; i++)
                {
                    Entity lane = lanes[i];
                    if (seedIndex.ContainsKey(lane) || !entityManager.HasComponent<NetLane>(lane))
                    {
                        continue;
                    }

                    NetLane netLane = entityManager.GetComponentData<NetLane>(lane);
                    int first = -1;
                    first = CollectSeeds(nodeToSeeds, parent, first, netLane.m_StartNode);
                    first = CollectSeeds(nodeToSeeds, parent, first, netLane.m_MiddleNode);
                    CollectSeeds(nodeToSeeds, parent, first, netLane.m_EndNode);
                }

                // ③ 组 → zone id
                Dictionary<int, int> rootToZone = new Dictionary<int, int>();
                for (int i = 0; i < seeds.Count; i++)
                {
                    int root = Find(parent, i);
                    int zone;
                    if (!rootToZone.TryGetValue(root, out zone))
                    {
                        zone = m_Zones.Count;
                        rootToZone[root] = zone;
                        m_Zones.Add(new List<Entity>(MaxZoneLanes));
                    }

                    if (m_Zones[zone].Count < MaxZoneLanes)
                    {
                        m_Zones[zone].Add(seeds[i]);
                    }

                    m_LaneToZone[seeds[i]] = zone;
                }

                // ④ 与种子相邻（共享节点）的非种子车道加入区组
                for (int i = 0; i < n; i++)
                {
                    Entity lane = lanes[i];
                    if (seedIndex.ContainsKey(lane) || !entityManager.HasComponent<NetLane>(lane))
                    {
                        continue;
                    }

                    NetLane netLane = entityManager.GetComponentData<NetLane>(lane);
                    // ZoneOfNode 返回的是 **zone id**（内部经 rootToZone 转换），不是并查集 root。
                    int zone = ZoneOfNode(nodeToSeeds, parent, rootToZone, netLane.m_StartNode);
                    if (zone < 0) zone = ZoneOfNode(nodeToSeeds, parent, rootToZone, netLane.m_MiddleNode);
                    if (zone < 0) zone = ZoneOfNode(nodeToSeeds, parent, rootToZone, netLane.m_EndNode);
                    if (zone < 0 || zone >= m_Zones.Count)
                    {
                        continue;
                    }

                    if (m_Zones[zone].Count < MaxZoneLanes)
                    {
                        m_Zones[zone].Add(lane);
                    }

                    m_LaneToZone[lane] = zone;
                }

                ModLog.Verbose("[P3] zones=" + m_Zones.Count + " seeds=" + seeds.Count + " trackLanes=" + n);
            }
            finally
            {
                lanes.Dispose();
            }
        }

        private static void AddSeedBuckets(EntityManager em, Dictionary<int, List<NodeEntry>> map, Entity lane, int seedIndex)
        {
            if (!em.HasComponent<NetLane>(lane))
            {
                return;
            }

            NetLane netLane = em.GetComponentData<NetLane>(lane);
            AddSeed(map, netLane.m_StartNode, seedIndex);
            AddSeed(map, netLane.m_MiddleNode, seedIndex);
            AddSeed(map, netLane.m_EndNode, seedIndex);
        }

        private static void AddSeed(Dictionary<int, List<NodeEntry>> map, PathNode node, int seedIndex)
        {
            int key = node.GetHashCode();
            List<NodeEntry> list;
            if (!map.TryGetValue(key, out list))
            {
                if (map.Count >= MaxNodeBuckets)
                {
                    return;
                }

                list = new List<NodeEntry>(2);
                map[key] = list;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].SeedIndex == seedIndex)
                {
                    return;
                }
            }

            if (list.Count < 16)
            {
                list.Add(new NodeEntry { Node = node, SeedIndex = seedIndex });
            }
        }

        private static void UnionSeedNodes(EntityManager em, Dictionary<int, List<NodeEntry>> map, int[] parent, Entity lane)
        {
            if (!em.HasComponent<NetLane>(lane))
            {
                return;
            }

            NetLane netLane = em.GetComponentData<NetLane>(lane);
            UnionNode(map, parent, netLane.m_StartNode);
            UnionNode(map, parent, netLane.m_MiddleNode);
            UnionNode(map, parent, netLane.m_EndNode);
        }

        private static void UnionNode(Dictionary<int, List<NodeEntry>> map, int[] parent, PathNode node)
        {
            List<NodeEntry> list;
            if (!map.TryGetValue(node.GetHashCode(), out list))
            {
                return;
            }

            int first = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].Node.EqualsIgnoreCurvePos(node))
                {
                    continue;
                }

                if (first < 0)
                {
                    first = list[i].SeedIndex;
                }
                else
                {
                    Union(parent, first, list[i].SeedIndex);
                }
            }
        }

        private static int CollectSeeds(Dictionary<int, List<NodeEntry>> map, int[] parent, int first, PathNode node)
        {
            List<NodeEntry> list;
            if (!map.TryGetValue(node.GetHashCode(), out list))
            {
                return first;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].Node.EqualsIgnoreCurvePos(node))
                {
                    continue;
                }

                if (first < 0)
                {
                    first = list[i].SeedIndex;
                }
                else
                {
                    Union(parent, first, list[i].SeedIndex);
                }
            }

            return first;
        }

        /// <summary>
        /// 取该节点所属的 **zone id**（不是并查集 root）。
        /// 之前这里直接返回 Find(...) 的 root，被步骤④当成 m_Zones 下标使用 → 越界崩溃。
        /// 现在统一经 rootToZone 转换；映射里没有就不加入区组（返回 -1）。
        /// </summary>
        private static int ZoneOfNode(
            Dictionary<int, List<NodeEntry>> map,
            int[] parent,
            Dictionary<int, int> rootToZone,
            PathNode node)
        {
            List<NodeEntry> list;
            if (!map.TryGetValue(node.GetHashCode(), out list))
            {
                return -1;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].Node.EqualsIgnoreCurvePos(node))
                {
                    continue;
                }

                int root = Find(parent, list[i].SeedIndex);
                int zone;
                return rootToZone.TryGetValue(root, out zone) ? zone : -1;
            }

            return -1;
        }

        private static int Find(int[] parent, int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a);
            int rb = Find(parent, b);
            if (ra != rb)
            {
                parent[rb] = ra;
            }
        }

        public bool TryGetZoneId(Entity lane, out int zoneId)
        {
            return m_LaneToZone.TryGetValue(lane, out zoneId);
        }

        public int GetZoneLaneCount(int zoneId)
        {
            return zoneId >= 0 && zoneId < m_Zones.Count ? m_Zones[zoneId].Count : 0;
        }

        /// <summary>
        /// 咽喉区忙闲判断（2026-09-27 完善）：任何成员 lane 上有非本车的 LaneReservation.m_Blocker 即忙，
        /// **但正在移动的挡路车不算**——它正在穿越咽喉，顷刻腾出；只有"停驻"的挡路车才值得 Hold。
        /// （对照第十九条 P2 的教训：占用判据必须区分"挡一下就走"与"真的停住"，否则咽喉区会被
        /// 途经车辆短暂预约拖成永久忙。）诊断：每个区组首条 busy 记一条节流日志。
        /// </summary>
        public bool IsBusy(EntityManager entityManager, int zoneId, Entity exceptVehicle)
        {
            return TryGetStandingBlocker(entityManager, zoneId, exceptVehicle, out _);
        }

        /// <summary>
        /// 取本区组第一个"停驻挡路车"（在移动的途经车不算，见 IsBusy 注释）；
        /// 找不到返回 false。ETA 感知放行由调用方（调度系统，持有快照）决定——服务层不依赖快照。
        /// </summary>
        public bool TryGetStandingBlocker(EntityManager entityManager, int zoneId, Entity exceptVehicle, out Entity blocker)
        {
            blocker = Entity.Null;
            if (zoneId < 0 || zoneId >= m_Zones.Count)
            {
                return false;
            }

            List<Entity> lanes = m_Zones[zoneId];
            for (int i = 0; i < lanes.Count; i++)
            {
                Entity lane = lanes[i];
                if (!entityManager.Exists(lane) || !entityManager.HasComponent<LaneReservation>(lane))
                {
                    continue;
                }

                Entity candidate = entityManager.GetComponentData<LaneReservation>(lane).m_Blocker;
                if (candidate == Entity.Null || candidate == exceptVehicle)
                {
                    continue;
                }

                if (entityManager.HasComponent<TrainNavigation>(candidate)
                    && entityManager.GetComponentData<TrainNavigation>(candidate).m_Speed > 0.5f)
                {
                    continue;   // 在移动 → 正在穿越，不算忙
                }

                blocker = candidate;
                if (m_BusyLogged.Add(zoneId))
                {
                    ModLog.Verbose("[P3] zone " + zoneId + " busy blocker=" + candidate.Index + " lanes=" + lanes.Count);
                }
                return true;
            }

            return false;
        }

        public override void ResetState()
        {
            m_LaneToZone.Clear();
            m_Zones.Clear();
            m_BusyLogged.Clear();
            m_LaneFirstLine.Clear();
            m_SharedLanes.Clear();
            LastSeedCount = 0;
            LastRebuildFrame = 0;
        }
    }
}
