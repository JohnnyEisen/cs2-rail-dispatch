namespace RailCapacityGuard.Runtime
{
    /// <summary>
    /// 图定基准表条目（P8，2026-09-28 起对玩家可见：列车 tooltip 第 5 行"本线基准"）。
    /// 由 TimetableDispatchSystem.ExportTimetable 每线每会话测算一次填充；
    /// 正点计数随每次离站结算增量累积（struct 值语义，写回字典）。
    /// </summary>
    public struct LineBaseline
    {
        /// <summary>本线站台数（RouteWaypoint buffer 实长）。</summary>
        public int Stops;

        /// <summary>实测基准圈时（分钟）= stops × (段中位 + 停站中位)。</summary>
        public float RoundTripMinutes;

        /// <summary>现有车数（RouteVehicle buffer 实长）。</summary>
        public int VehiclesNow;

        /// <summary>需要车底数 ⌈圈时 / 发车间隔⌉（国铁运行图要素 N = T周/I）。</summary>
        public float NeedFleet;

        /// <summary>实际间隔（分钟）= 圈时 / 实有车数——车辆不足时主导图定节奏。</summary>
        public float ActualIntervalMinutes;

        /// <summary>旅行速度 v旅 = 圈内里程 / 圈时（km/h，游戏 UI 口径）。</summary>
        public float TravelSpeedKmh;

        /// <summary>正点离站次数（晚点 ≤ 0.5 游戏分钟）。</summary>
        public int OnTimeCount;

        /// <summary>晚点离站次数（> 0.5 游戏分钟）。</summary>
        public int LateCount;
    }
}
