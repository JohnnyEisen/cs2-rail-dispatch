namespace RailCapacityGuard.Runtime
{
    /// <summary>
    /// 国铁对齐调优常量的唯一归拢处（2026-09-28 整合，此前散落在 TimetableDispatchSystem）。
    /// 每个常量都有出处（日志实证/反编译/玩家实测），改值前先看注释里的判据。
    ///
    /// 配套换算（不在本类，位置备注）：
    ///   * UnitConversion.kWorldSpeedToKmh = 1.8f      —— 世界速度→UI km/h（玩家实测校准）
    ///   * SegmentTimeService.kMaxLegFrames = 262144f  —— leg 护栏上限（1 游戏日）
    ///   * SegmentTimeService.kRing = 5                —— 实测 leg/dwell 环形样本数
    /// </summary>
    internal static class CnRailwayTuning
    {
        /// <summary>
        /// 咽喉 zone 错峰窗（帧）：他线刚放行后本线需等待的窗口。
        /// 量级判据 = 一次咽喉穿越的时间（几游戏秒）；曾误用 MinHeadwayFrames（2 游戏分钟）
        /// 导致 53 次过度 Hold——**等待窗必须匹配被控对象的物理量级**。
        /// </summary>
        public const float kZoneStaggerFrames = 128f;

        /// <summary>正点阈值（帧）：晚点 ≤ 0.5 游戏分钟（91 帧）仍算正点（近似"晚点 1 分钟内不计"）。</summary>
        public const float kPunctualFrames = 91f;

        /// <summary>晚点恢复——压缩停站系数：晚点车编图时计划停站按此比例压缩（下限见调用处），
        /// 链式图定自然回落，否则晚点永久传播。0.5 = 半数停站时分用于恢复。</summary>
        public const float kDwellCompression = 0.5f;

        /// <summary>旅行速度里程求和的段数上界（PathInformation.m_Distance 逐段累加）。</summary>
        public const int kMaxDistanceSegments = 256;
    }
}
