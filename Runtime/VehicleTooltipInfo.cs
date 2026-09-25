using Unity.Entities;

namespace RailCapacityGuard.Runtime
{
    /// <summary>tooltip 状态分层（修复 4）：三层文案靠它区分。</summary>
    public enum VehicleStateKind : byte
    {
        Running = 0,          // 运行中（本 Mod 未干预）
        WaitingTimetable = 1, // 待发：在站台等图定时刻
        WaitingPlatform = 2,  // 待避：前方站台被占
        WaitingThroat = 3,    // 待避：咽喉区占用
        HoldLimit = 4,        // 待发：已接近等待上限
        Releasing = 5,        // 放行：立即发车
        DataUnavailable = 6,  // 交回原版调度（真读不到数据）
        SegmentBusy = 7,      // 待避：前方区间被占（缺陷 1）
        HoldLimitRelease = 8, // 放行：等待超限，强制放行（缺陷 6）
        StoppedEnRoute = 10,  // 等待中：车在区间里停住了（速度≈0），原因见 reason（问题二）
        ForcedDepart = 11,    // 强制发车：停站超上限（原版 boarding 卡死兜底）
        Boarding = 12,        // 上下客中（原版 Boarding 位仍置位，我们完全不干预）
        ScheduleUnknown = 13, // 站间运行时间未测出 → 图定"待定"，交回原版
    }

    /// <summary>
    /// tooltip 只读快照：由 TimetableDispatchSystem 在决策时写入，TrainTooltipSystem 只读。
    /// 纯托管、不进存档（读档时随 dispatch 的 per-load 清理一起清空）。
    /// </summary>
    public struct VehicleTooltipInfo
    {
        /// <summary>状态分层（修复 4）。</summary>
        public VehicleStateKind Kind;

        /// <summary>该车所在线路是否被本 Mod 接管。</summary>
        public bool   IsManaged;

        public Entity Line;

        /// <summary>时间层算出的**本站图定发车帧**（= 上一站实际发车 + 站间实测运行 + 上一站实测停站）。</summary>
        public uint   PlannedFrame;

        /// <summary>本 tick 决策实际会写入的发车帧（Hold 时可能被推迟）。</summary>
        public uint   TargetFrame;

        /// <summary>决策发生的帧。</summary>
        public uint   NowFrame;

        /// <summary>本线站间运行时间（帧，实测 EMA）——新设计用它替代旧的"发车间隔"。</summary>
        public float  SegmentRunFrames;

        /// <summary>本车到下一站的 ETA（帧）；-1 = 读不到（缺陷 5：路上车辆也显示到达时刻）。</summary>
        public float  EtaFrames;

        /// <summary>本车速度（m/s，来自 Game.Vehicles.TrainNavigation.m_Speed）；-1 = 读不到（问题二：区分在跑与停住）。</summary>
        public float  Speed;

        /// <summary>boarding 期间已停站帧数（now − StopEnterFrame）；-1 = 未知。</summary>
        public float  DwellFrames;

        /// <summary>停站硬上限（帧，来自 MaxBoardingMinutes）；-1 = 未知。</summary>
        public float  BoardingCapFrames;

        /// <summary>本 tick 是否被按住（时间层/空间层/咽喉区）。</summary>
        public bool   Held;

        /// <summary>决策原因（与 Verbose 日志同一份文本；NoData 表示数据不可用 → 不干预）。</summary>
        public string Reason;
    }
}
