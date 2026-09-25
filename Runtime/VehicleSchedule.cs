using Unity.Entities;

namespace RailCapacityGuard.Runtime
{
    /// <summary>
    /// 每辆车的"图定时刻表"（P7 新设计）。**纯托管、不进存档**。
    ///
    /// 现实时刻表映射：
    ///   本站图定发车 = 上一站实际发车 + 站间运行时间 + 上一站停站时间
    /// 全部用**实测帧数**（本车上一段实测 leg + 上一站实测 dwell），不依赖任何原版 interval 字段。
    /// </summary>
    public struct VehicleSchedule
    {
        /// <summary>本车当前这一站的图定发车帧（进入站台等待时算一次，之后锁定）。</summary>
        public uint PlannedDepartFrame;

        /// <summary>本车上一站的实际发车帧（= 下一站图定的基准）。</summary>
        public uint LastDepartFrame;

        /// <summary>本车上一段实测运行时间（帧）；0 = 还没测到。</summary>
        public float LastLegFrames;

        /// <summary>本车上一站实测停站时间（帧）；0 = 还没测到。</summary>
        public float LastDwellFrames;

        /// <summary>进入当前站台的帧（用于测停站时间）。</summary>
        public uint StopEnterFrame;

        /// <summary>当前是否处于"站台等待"状态。</summary>
        public bool AtStop;

        /// <summary>停站超上限的告警是否已打过（避免每 tick 刷屏）。</summary>
        public bool ForcedDepartLogged;

        /// <summary>本站图定算不出来（站间运行时间三级来源全失效）→ 待定，交回原版。</summary>
        public bool ScheduleUnknown;

        /// <summary>本段运行时间估计（帧，来自 PathInformation.m_Duration）；0 = 没读到。</summary>
        public float LegEstimateFrames;

        /// <summary>本段估计是否通过自检（比值 0.5–2.0）。</summary>
        public bool LegEstimateOk;

        /// <summary>本段起点帧（= 实际写入的发车帧），用于算"本段剩余帧"。</summary>
        public uint LegStartFrame;

        /// <summary>连续多少个 tick 没被判定为"在站台"（闩锁用；≥8 才解除本站计划）。</summary>
        public int NotAtStopTicks;
    }
}
