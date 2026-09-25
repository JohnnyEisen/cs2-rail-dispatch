using Unity.Entities;

namespace RailCapacityGuard.Runtime
{
    /// <summary>一辆车在某一 tick 的发车决策结果（诊断用）。</summary>
    public enum DepartureDecision : byte
    {
        NoData = 0,   // 数据不可用 → 不碰（交回原版）
        NoChange = 1, // 无需写入（与原值相同）
        Hold = 2,     // 按住（时间层未到 / 空间层被占 / 咽喉区忙）
        Depart = 3    // 写入发车帧
    }

    /// <summary>
    /// 单车决策快照（诊断/日志用）。不写入任何 ECS 组件。
    /// </summary>
    public struct VehicleDecision
    {
        public Entity Vehicle;
        public Entity PlatformLane;
        public uint   Target;
        public DepartureDecision Decision;
        public bool   EarlyDeparture;
        public float  EtaFrames;
        public float  SlackFrames;
        public string Reason;
    }

    /// <summary>
    /// 每线路的运行时状态。**纯托管、不进存档**（拍板：不用 AddComponentData / 不实现 ISerializable）。
    /// 读档时由 PreDeserialize → ServiceRegistry.ResetAll() 清空，读档后重新推导。
    /// </summary>
    public struct LineRuntimeState
    {
        public bool   Initialized;
        public Entity Line;

        // --- 站点解析（LineStationResolver） ---
        public Entity TerminusWaypoint;   // 线路 RouteWaypoint buffer 的最后一项（见解析服务里的假设说明）
        public uint   LastResolveFrame;

        // --- 时刻（帧）——P7 新设计：不再有"圈时/车辆数"的 interval ---
        public float  SegmentRunFrames;   // 本线"站间运行时间"的实测 EMA（帧）；0 = 还没测到
        public float  MinHeadwayFrames;   // 冲突检查用的最小发车间距（= MinHeadwayMinutes × fpm，与 interval 无关）
        public float  MaxEarlyFrames;     // = SegmentRunFrames × MaxEarlyPercent
        public uint   LastWrittenDepartureFrame; // 本 Mod 最近一次写入的发车帧（作为"上一班实际发车帧"的基准）
        public uint   RetryAfterFrame;    // 站台被占后最早重试帧

        // --- 观测计数（供车队四道闸与诊断） ---
        public int    HoldCount;
        public int    EarlyCount;
        public int    PostponeStreak;     // 连续推迟次数（超一个 headway 后告警并交回原版）
        public bool   CapacityBlocked;
        public bool   DataUnavailable;

        // --- 车队双向自适应（默认关闭） ---
        public int    FleetTarget;
        public int    FleetUpStreak;
        public int    FleetDownStreak;
        public uint   FleetLastChangeFrame;
    }
}
