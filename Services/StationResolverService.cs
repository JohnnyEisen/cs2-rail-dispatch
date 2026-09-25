using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using RailCapacityGuard.Utils;
using NetTrackLane = Game.Net.TrackLane;

namespace RailCapacityGuard.Services
{
    /// <summary>
    /// 站点/站台解析（自写）。所有字段对照 dump：
    ///   * 站台轨道识别：TrackLane.m_Flags & TrackLaneFlags.Station（dump:20620/20638）
    ///   * 车辆→前方站台：TrainNavigationLane.m_Lane（车辆侧缓冲区，长度上界实测 11）
    ///     备选：TrainCurrentLane.m_Front/.m_Rear/.m_FrontCache.m_Lane（已实测可用）
    ///   * 线路终点站：RouteWaypoint buffer → m_Waypoint（dump:50130）
    ///   * 线路站台数量：PathTargets 宿主区段（Owner.m_Owner == line, dump:6620）的
    ///     m_StartLane/m_EndLane（dump:49840-49841）中属于站台轨道的去重计数
    /// </summary>
    public sealed class StationResolverService : RailGuardServiceBase
    {
        private const int MaxNavLanes = 32;
        private const int MaxPathTargetsHosts = 256;

        public StationResolverService(World world) : base(world)
        {
        }

        public override string Name => "P4.StationResolver";

        public bool IsStationTrack(EntityManager entityManager, Entity lane)
        {
            if (lane == Entity.Null || !entityManager.Exists(lane) || !entityManager.HasComponent<NetTrackLane>(lane))
            {
                return false;
            }

            NetTrackLane track = entityManager.GetComponentData<NetTrackLane>(lane);
            return (track.m_Flags & Game.Net.TrackLaneFlags.Station) != 0;
        }

        /// <summary>车辆前方最近的站台轨道：优先扫 TrainNavigationLane 缓冲，其次看当前所在车道。</summary>
        public bool TryGetUpcomingStationLane(EntityManager entityManager, Entity vehicle, out Entity lane)
        {
            lane = Entity.Null;
            if (vehicle == Entity.Null || !entityManager.Exists(vehicle))
            {
                return false;
            }

            if (entityManager.HasComponent<TrainNavigationLane>(vehicle))
            {
                DynamicBuffer<TrainNavigationLane> nav = entityManager.GetBuffer<TrainNavigationLane>(vehicle, true);
                int count = nav.Length > MaxNavLanes ? MaxNavLanes : nav.Length;
                for (int i = 0; i < count; i++)
                {
                    if (IsStationTrack(entityManager, nav[i].m_Lane))
                    {
                        lane = nav[i].m_Lane;
                        return true;
                    }
                }
            }

            return TryGetCurrentStationLane(entityManager, vehicle, out lane);
        }

        public bool TryGetCurrentStationLane(EntityManager entityManager, Entity vehicle, out Entity lane)
        {
            lane = Entity.Null;
            if (vehicle == Entity.Null || !entityManager.Exists(vehicle) || !entityManager.HasComponent<TrainCurrentLane>(vehicle))
            {
                return false;
            }

            TrainCurrentLane current = entityManager.GetComponentData<TrainCurrentLane>(vehicle);
            if (IsStationTrack(entityManager, current.m_Front.m_Lane))
            {
                lane = current.m_Front.m_Lane;
                return true;
            }

            if (IsStationTrack(entityManager, current.m_Rear.m_Lane))
            {
                lane = current.m_Rear.m_Lane;
                return true;
            }

            if (IsStationTrack(entityManager, current.m_FrontCache.m_Lane))
            {
                lane = current.m_FrontCache.m_Lane;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 终点站锚点 = RouteWaypoint buffer 的最后一项。
        /// **假设**：buffer 顺序等于线路站序、最后一项是终点站（需运行时验证；解析失败时返回 false，
        /// 调用方走"数据不可用 → 不碰"分支）。
        /// </summary>
        public bool TryGetTerminusWaypoint(EntityManager entityManager, Entity line, out Entity waypoint)
        {
            return TryGetEndWaypoint(entityManager, line, true, out waypoint);
        }

        /// <summary>始发站（m_Index 最小者）。</summary>
        public bool TryGetOriginWaypoint(EntityManager entityManager, Entity line, out Entity waypoint)
        {
            return TryGetEndWaypoint(entityManager, line, false, out waypoint);
        }

        /// <summary>
        /// 问题四：**不再依赖 RouteWaypoint buffer 顺序**。
        /// 实测 buffer 首项/末项的实体 Index 大小与站序无关（firstWp=158508 / lastWp=158496），
        /// 因为 Entity.Index 只是分配顺序。真正的站序在 @@Game.Routes.Waypoint.m_Index@@（dump:50399，
        /// 该组件唯一字段）上，因此取 m_Index 最大者为终点站、最小者为始发站。
        /// </summary>
        private bool TryGetEndWaypoint(EntityManager entityManager, Entity line, bool terminus, out Entity waypoint)
        {
            waypoint = Entity.Null;
            if (line == Entity.Null || !entityManager.Exists(line) || !entityManager.HasComponent<Game.Routes.RouteWaypoint>(line))
            {
                return false;
            }

            DynamicBuffer<Game.Routes.RouteWaypoint> waypoints =
                entityManager.GetBuffer<Game.Routes.RouteWaypoint>(line, true);
            if (waypoints.Length == 0)
            {
                return false;
            }

            int best = terminus ? int.MinValue : int.MaxValue;
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity candidate = waypoints[i].m_Waypoint;
                if (candidate == Entity.Null || !entityManager.Exists(candidate)
                    || !entityManager.HasComponent<Game.Routes.Waypoint>(candidate))
                {
                    continue;
                }

                int index = entityManager.GetComponentData<Game.Routes.Waypoint>(candidate).m_Index;
                if (terminus ? index > best : index < best)
                {
                    best = index;
                    waypoint = candidate;
                }
            }

            return waypoint != Entity.Null;
        }

        /// <summary>本线路的站台轨道去重数量（用于诊断；每次调用会遍历 PathTargets 宿主，上界 MaxPathTargetsHosts）。</summary>
        public int CountPlatformLanes(EntityManager entityManager, Entity line)
        {
            int count = 0;
            EntityQuery query = entityManager.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<Game.Routes.PathTargets>() });
            NativeArray<Entity> hosts = query.ToEntityArray(Allocator.Temp);
            try
            {
                int n = hosts.Length > MaxPathTargetsHosts ? MaxPathTargetsHosts : hosts.Length;
                Entity seenA = Entity.Null;
                Entity seenB = Entity.Null;
                for (int i = 0; i < n; i++)
                {
                    Entity host = hosts[i];
                    if (!entityManager.HasComponent<Game.Common.Owner>(host))
                    {
                        continue;
                    }

                    if (entityManager.GetComponentData<Game.Common.Owner>(host).m_Owner != line)
                    {
                        continue;
                    }

                    Game.Routes.PathTargets targets = entityManager.GetComponentData<Game.Routes.PathTargets>(host);
                    if (IsStationTrack(entityManager, targets.m_StartLane) && targets.m_StartLane != seenA && targets.m_StartLane != seenB)
                    {
                        seenA = targets.m_StartLane;
                        count++;
                    }

                    if (IsStationTrack(entityManager, targets.m_EndLane) && targets.m_EndLane != seenA && targets.m_EndLane != seenB)
                    {
                        seenB = targets.m_EndLane;
                        count++;
                    }
                }
            }
            finally
            {
                hosts.Dispose();
            }

            return count;
        }

        public override void ResetState()
        {
            // 无缓存状态
        }
    }
}
