using System.Collections.Generic;
using Game.Pathfind;
using Game.Routes;
using Unity.Entities;
using RailCapacityGuard.Utils;

namespace RailCapacityGuard.Services
{
    /// <summary>
    /// 站间运行时间服务（P7 时间层数据源，2026-09-25 实测后定稿）。
    ///
    ///   主源：车辆 Game.Pathfind.PathInformation.m_Duration（route units，dump 已核实字段），
    ///         **每段读一次**（我们真正写发车帧那一刻）；实测证明该值停站期间恒定、跨车辆同 dest 一致。
    ///   单位：frames = units × UnitMinutes × FramesPerMinute
    ///         （TT ScheduleMath.cs:176 × UnitMinutes；TimebaseSystem.cs:32-33  1 unit = 60 帧）。
    ///   回退：① 本线最近 kRing 段"真实 leg"（发车帧→下一站进站帧）的中位数
    ///         ② Σ RouteSegment.m_Duration ÷ 段数（TT HourlyFleetSystem.cs:202-235 同口径）
    ///         ③ 0 = 未测出（调用方走"待定/交回原版"）
    /// 纯托管、不进存档、不做 Burst。
    /// </summary>
    public sealed class SegmentTimeService
    {
        public const int kRing = 5;                 // 5 段足够抗离群（TT 用 10+ 是因为它要推车队，我们不推）
        private const int kMaxSegments = 256;
        private const float kMinDurationUnits = 0.5f;

        private readonly Dictionary<Entity, float[]> m_Ring = new Dictionary<Entity, float[]>(64);
        private readonly Dictionary<Entity, int> m_Count = new Dictionary<Entity, int>(64);
        private readonly Dictionary<Entity, int> m_Head = new Dictionary<Entity, int>(64);
        private readonly Dictionary<Entity, float> m_MeanLeg = new Dictionary<Entity, float>(64);
        private readonly Dictionary<Entity, uint> m_MeanFrame = new Dictionary<Entity, uint>(64);
        private readonly float[] m_Scratch = new float[kRing];

        public void Clear()
        {
            m_Ring.Clear();
            m_Count.Clear();
            m_Head.Clear();
            m_MeanLeg.Clear();
            m_MeanFrame.Clear();
        }

        /// <summary>主源：读车辆当前 m_Duration（route units → 帧）。不可用或小于下限返回 false。</summary>
        public bool TryReadVehicleLegFrames(EntityManager em, Entity vehicle, float unitMinutes, float fpm, float minFrames, out float frames)
        {
            frames = 0f;
            if (unitMinutes <= 0f || fpm <= 0f || !em.HasComponent<PathInformation>(vehicle))
            {
                return false;
            }

            float units = em.GetComponentData<PathInformation>(vehicle).m_Duration;
            if (units < kMinDurationUnits)
            {
                return false;
            }

            frames = UnitConversion.UnitsToFrames(units, unitMinutes, fpm);
            return frames >= minFrames;
        }

        /// <summary>记录一条"真实 leg"（发车帧 → 下一站进站帧），用于回退 ①。</summary>
        public void RecordLeg(Entity line, float frames)
        {
            if (frames <= 0f)
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
        /// 回退 ②：Σ RouteSegment.m_Duration ÷ 段数（TT HourlyFleetSystem.cs:202-235 同口径）。
        /// 每 256 帧最多重算一次/线路（懒加载，常态零开销）。
        /// </summary>
        public bool TryGetLineMeanLegFrames(EntityManager em, Entity line, float unitMinutes, float fpm, float minFrames, uint now, out float frames)
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

            float total = 0f;
            int used = 0;
            for (int i = 0; i < count; i++)
            {
                Entity segment = segments[i].m_Segment;
                if (segment == Entity.Null || !em.Exists(segment) || !em.HasComponent<PathInformation>(segment))
                {
                    continue;
                }

                total += em.GetComponentData<PathInformation>(segment).m_Duration;
                used++;
            }

            if (used <= 0 || total <= 0f)
            {
                return false;
            }

            frames = UnitConversion.UnitsToFrames(total / used, unitMinutes, fpm);
            m_MeanLeg[line] = frames;
            m_MeanFrame[line] = now;
            return frames >= minFrames;
        }
    }
}
