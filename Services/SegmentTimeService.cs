using System.Collections.Generic;
using Game.Pathfind;
using Game.Routes;
using Unity.Entities;
using RailCapacityGuard.Utils;

namespace RailCapacityGuard.Services
{
    /// <summary>
    /// 站间运行时间服务（P7 时间层数据源，2026-09-26 反编译后修订）。
    ///
    ///   主源：本线最近 kRing 段"真实 leg"（发车帧→下一站进站帧）的中位数（ring 只装实测）。
    ///   回退：本线 RouteSegment.m_Duration 的**中位数**（RoutePathReadySystem 证实为 per-segment
    ///         单段结果；均值已被 D3 实测否证——折返/绕路等离群段把 Σ÷n 抬高 3–10×）。
    ///   车辆自身 PathInformation.m_Duration 已证伪弃用：反编译证实它是寻路完成时写入的
    ///         整条路径静态快照（PathfindJobs.CreatePath Σ边长/边限速；ProcessResultsJob 一次性写），
    ///         既不是剩余时长也不是本段，且不随行进递减。
    ///   单位：frames = units × UnitMinutes × FramesPerMinute（1 route unit = 60 帧）。
    /// 纯托管、不进存档、不做 Burst。
    /// </summary>
    public sealed class SegmentTimeService
    {
        public const int kRing = 5;                 // 5 段足够抗离群（TT 用 10+ 是因为它要推车队，我们不推）
        private const int kMaxSegments = 256;
        private const float kMinDurationUnits = 0.5f;

        // ── leg 护栏（2026-09-26 leg=17895760 事故后新增）──────────────────────────
        // 上限 = 1 游戏日（1440 min × 182.0444 fpm = 262144 帧）：任何"段运行时间"超过一天都是垃圾
        //（典型来源：原版 BeginBoarding 用 uint 减法算 (arrival − m_DepartureFrame)，而 m_DepartureFrame
        //  在 boarding 开始时被写成未来帧（RouteUtils.CalculateDepartureFrame / simFrame+60），车若早于
        //  该帧到达下一站 → uint 回绕 → ~4.29E9 差值 ÷60 存进 m_AverageTravelTime 的 EMA，永久污染）。
        // 下界 = 0.5×fpm（半游戏分钟）：短于此的"段"视为噪声。
        public const float kMaxLegFrames = 262144f;

        /// <summary>把一段"帧数"夹取到合法区间 [0.5×fpm, kMaxLegFrames]（护栏唯一入口）。</summary>
        public static float ClampLegFrames(float frames, float fpm)
        {
            float low = 0.5f * fpm;
            if (frames < low)
            {
                return low;
            }
            if (frames > kMaxLegFrames)
            {
                return kMaxLegFrames;
            }
            return frames;
        }

        private readonly Dictionary<Entity, float[]> m_Ring = new Dictionary<Entity, float[]>(64);
        private readonly Dictionary<Entity, int> m_Count = new Dictionary<Entity, int>(64);
        private readonly Dictionary<Entity, int> m_Head = new Dictionary<Entity, int>(64);
        private readonly Dictionary<Entity, float> m_MeanLeg = new Dictionary<Entity, float>(64);
        private readonly Dictionary<Entity, uint> m_MeanFrame = new Dictionary<Entity, uint>(64);
        // P8 基准时刻表：停站时间 ring（线路级；分站聚合留待需要时再做）
        private readonly Dictionary<Entity, float[]> m_DwellRing = new Dictionary<Entity, float[]>(64);
        private readonly Dictionary<Entity, int> m_DwellCount = new Dictionary<Entity, int>(64);
        private readonly Dictionary<Entity, int> m_DwellHead = new Dictionary<Entity, int>(64);
        private readonly float[] m_Scratch = new float[kRing];
        private readonly float[] m_ScratchWide = new float[kMaxSegments];
        private readonly float[] m_SegmentScratch = new float[kMaxSegments];

        public void Clear()
        {
            m_Ring.Clear();
            m_Count.Clear();
            m_Head.Clear();
            m_MeanLeg.Clear();
            m_MeanFrame.Clear();
            m_DwellRing.Clear();
            m_DwellCount.Clear();
            m_DwellHead.Clear();
        }

        /// <summary>记录一次实测停站时间（帧，P8 基准时刻表数据源），护栏同 leg。</summary>
        public void RecordDwell(Entity line, float frames)
        {
            if (frames <= 0f || frames > kMaxLegFrames)
            {
                return;
            }

            float[] ring;
            if (!m_DwellRing.TryGetValue(line, out ring))
            {
                ring = new float[kRing];
                m_DwellRing[line] = ring;
                m_DwellCount[line] = 0;
                m_DwellHead[line] = 0;
            }

            int head = m_DwellHead[line];
            ring[head] = frames;
            m_DwellHead[line] = (head + 1) % kRing;
            int count = m_DwellCount[line];
            if (count < kRing)
            {
                m_DwellCount[line] = count + 1;
            }
        }

        /// <summary>本线最近 kRing 次实测停站的中位数（帧）；样本不足返回 0。</summary>
        public float GetMedianDwell(Entity line)
        {
            float[] ring;
            int count;
            if (!m_DwellRing.TryGetValue(line, out ring) || !m_DwellCount.TryGetValue(line, out count) || count <= 0)
            {
                return 0f;
            }

            for (int i = 0; i < count; i++)
            {
                m_Scratch[i] = ring[i];
            }

            for (int i = 1; i < count; i++)
            {
                float key = m_Scratch[i];
                int j = i - 1;
                while (j >= 0 && m_Scratch[j] > key)
                {
                    m_Scratch[j + 1] = m_Scratch[j];
                    j--;
                }
                m_Scratch[j + 1] = key;
            }

            return m_Scratch[count / 2];
        }

        /// <summary>
        /// 记录一条"真实 leg"（发车帧 → 下一站进站帧），用于回退 ①。
        /// 护栏：> kMaxLegFrames（1 游戏日）的样本是垃圾（回绕/跨日停摆），直接丢弃不入 ring。
        /// </summary>
        public void RecordLeg(Entity line, float frames)
        {
            if (frames <= 0f || frames > kMaxLegFrames)
            {
                return;
            }

            float[] ring;
            if (!m_Ring.TryGetValue(line, out ring))
            {
                ring = new float[kRing];
                m_Ring[line] = ring;
                m_Count[line] = 0;
                m_Head[line] = 0;
            }

            int head = m_Head[line];
            ring[head] = frames;
            m_Head[line] = (head + 1) % kRing;
            int count = m_Count[line];
            if (count < kRing)
            {
                m_Count[line] = count + 1;
            }
        }

        /// <summary>本线 ring 里已有的真实 leg 样本数（诊断用）。</summary>
        public int GetLineSampleCount(Entity line)
        {
                int count;
                return m_Count.TryGetValue(line, out count) ? count : 0;
        }

        /// <summary>回退 ①：本线最近 kRing 段真实 leg 的中位数；样本不足返回 0。</summary>
        public float GetLineMedian(Entity line)
        {
            float[] ring;
            int count;
            if (!m_Ring.TryGetValue(line, out ring) || !m_Count.TryGetValue(line, out count) || count <= 0)
            {
                return 0f;
            }

            for (int i = 0; i < count; i++)
            {
                m_Scratch[i] = ring[i];
            }

            for (int i = 1; i < count; i++)
            {
                float key = m_Scratch[i];
                int j = i - 1;
                while (j >= 0 && m_Scratch[j] > key)
                {
                    m_Scratch[j + 1] = m_Scratch[j];
                    j--;
                }
                m_Scratch[j + 1] = key;
            }

            return m_Scratch[count / 2];
        }

        /// <summary>
        /// 数据源（原版自维护，上客期间也在刷新）：本线各 waypoint 上
        /// Game.Routes.VehicleTiming.m_AverageTravelTime 的中位数。
        /// 单位（反编译复核 2026-09-26 晚，修正 9d877a7 的"帧"误判）：
        ///   RouteUtils.UpdateAverageTravelTime = (arrivalFrame − departureFrame) / 60f ⇒ 存的是 **route units**
        ///   （与 CalculateDepartureFrame 的 ÷60/×60 对称、与 TT TimebaseSystem "60 sim-frames per unit" 一致），
        ///   必须经 UnitConversion.UnitsToFrames 转帧后再用；此前把 units 当帧用 → 差 60×。
        /// 污染防护：TransportBoardingHelpers.BeginBoarding:345-367 用 **uint 减法**，而 m_DepartureFrame
        ///   在 boarding 开始时被写成未来帧 ⇒ 车早于图定到达时差值回绕成 ~4.29E9 垃圾，EMA 0.5 长期保留。
        ///   故每个样本先转帧、再只收 [0.5×fpm, kMaxLegFrames] 区间内的样本，区间外（含回绕垃圾）直接丢弃。
        /// </summary>
        public bool TryGetLineVehicleTimingMedian(EntityManager em, Entity line, float unitMinutes, float fpm, float minFrames, out float frames)
        {
                frames = 0f;
                if (line == Entity.Null || !em.Exists(line) || !em.HasBuffer<Game.Routes.RouteWaypoint>(line)
                    || unitMinutes <= 0f || fpm <= 0f)
                {
                        return false;
                }

                DynamicBuffer<Game.Routes.RouteWaypoint> waypoints = em.GetBuffer<Game.Routes.RouteWaypoint>(line, true);
                int count = waypoints.Length > kMaxSegments ? kMaxSegments : waypoints.Length;
                float low = 0.5f * fpm;
                int used = 0;
                for (int i = 0; i < count; i++)
                {
                        Entity wp = waypoints[i].m_Waypoint;
                        if (wp == Entity.Null || !em.Exists(wp) || !em.HasComponent<Game.Routes.VehicleTiming>(wp))
                        {
                                continue;
                        }

                        float framesSample = UnitConversion.UnitsToFrames(em.GetComponentData<Game.Routes.VehicleTiming>(wp).m_AverageTravelTime, unitMinutes, fpm);
                        if (framesSample >= low && framesSample <= kMaxLegFrames)
                        {
                                m_ScratchWide[used++] = framesSample;
                        }
                }

                if (used <= 0)
                {
                        return false;
                }

                for (int i = 1; i < used; i++)
                {
                        float key = m_ScratchWide[i];
                        int j = i - 1;
                        while (j >= 0 && m_ScratchWide[j] > key)
                        {
                                m_ScratchWide[j + 1] = m_ScratchWide[j];
                                j--;
                        }
                        m_ScratchWide[j + 1] = key;
                }

                frames = m_ScratchWide[used / 2];
                return frames >= minFrames;
        }
        /// <summary>
        /// 回退 ②：本线 RouteSegment.m_Duration 的**中位数**（不再是均值——D3 实测均值被
        /// 折返/绕路等离群段抬高 3–10×；RoutePathReadySystem 证实 segment PathInformation 为
        /// per-segment 单段结果，故中位数可直接用作 leg 量级估计）。
        /// 每 256 帧最多重算一次/线路（懒加载，常态零开销）。
        /// </summary>
        public bool TryGetLineMedianLegFrames(EntityManager em, Entity line, float unitMinutes, float fpm, float minFrames, uint now, out float frames)
        {
            frames = 0f;
            float cached;
            uint cachedFrame;
            if (m_MeanLeg.TryGetValue(line, out cached) && m_MeanFrame.TryGetValue(line, out cachedFrame)
                && now - cachedFrame < 256u)
            {
                frames = cached;
                return frames >= minFrames;
            }

            if (!em.HasBuffer<RouteSegment>(line) || unitMinutes <= 0f || fpm <= 0f)
            {
                return false;
            }

            DynamicBuffer<RouteSegment> segments = em.GetBuffer<RouteSegment>(line, true);
            int count = segments.Length > kMaxSegments ? kMaxSegments : segments.Length;
            if (count <= 0)
            {
                return false;
            }

            int used = 0;
            for (int i = 0; i < count; i++)
            {
                Entity segment = segments[i].m_Segment;
                if (segment == Entity.Null || !em.Exists(segment) || !em.HasComponent<PathInformation>(segment))
                {
                    continue;
                }

                float units = em.GetComponentData<PathInformation>(segment).m_Duration;
                if (units < kMinDurationUnits)
                {
                    continue;   // 失败/异常段（如 -1）不参与
                }

                m_SegmentScratch[used++] = units;
            }

            if (used <= 0)
            {
                return false;
            }

            // 插入排序（used ≤ 256，每线每 256 帧一次，开销可忽略）
            for (int i = 1; i < used; i++)
            {
                float key = m_SegmentScratch[i];
                int j = i - 1;
                while (j >= 0 && m_SegmentScratch[j] > key)
                {
                    m_SegmentScratch[j + 1] = m_SegmentScratch[j];
                    j--;
                }
                m_SegmentScratch[j + 1] = key;
            }

            frames = SegmentTimeService.ClampLegFrames(UnitConversion.UnitsToFrames(m_SegmentScratch[used / 2], unitMinutes, fpm), fpm);
            m_MeanLeg[line] = frames;
            m_MeanFrame[line] = now;
            return frames >= minFrames;
        }
    }
}
