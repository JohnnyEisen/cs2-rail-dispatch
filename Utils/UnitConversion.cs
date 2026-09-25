namespace RailCapacityGuard.Utils
{
    /// <summary>
    /// 单位换算的唯一入口（清单要求：所有换算都走这里）。
    ///
    /// 单位事实（本地 TT/MIT 源码核实，非猜测）：
    ///   * @@TransportLine.m_VehicleInterval@@ 与 @@PathInformation.m_Duration@@ 都是 **route duration unit**
    ///     - TT @@HourlyFleetSystem.cs:94@@ 把 @@CalculateVehicleInterval(duration, target)@@ 的结果与
    ///       @@TransportLineData.m_DefaultVehicleInterval@@ 相减（同一单位）
    ///     - TT @@HourlyFleetSystem.cs:202/@:230@@ 的 @@ComputeStableDuration@@ 直接对 @@PathInformation.m_Duration@@ 求和
    ///     - TT @@ScheduleMath.cs:176@@ @@RoundTripMinutes(units, unitMinutes) => units * unitMinutes@@
    ///     - TT @@TimebaseSystem.cs:32-33@@ "60 sim-frames per route duration unit"
    ///   * @@UnitMinutes = 86400 / ticksPerDay@@（vanilla = 0.32958984375）
    ///   * @@FramesPerMinute = ticksPerDay / 1440@@（vanilla = 182.0444489）
    ///
    /// 因此 1 unit = UnitMinutes × FramesPerMinute = **60 帧**（仅 vanilla 日长成立；
    /// 慢时钟 Mod 下 UnitMinutes 会变，公式仍然正确）。
    /// 我们此前误把 unit 当分钟用（× FramesPerMinute）→ 放大 1/UnitMinutes = 3.033 倍（RC-1）。
    /// </summary>
    internal static class UnitConversion
    {
        /// <summary>route units → 帧。清单要求的静态工具方法。</summary>
        public static float UnitsToFrames(float units, float unitMinutes, float framesPerMinute)
        {
            return units * unitMinutes * framesPerMinute;
        }

        /// <summary>route units → 游戏内分钟。</summary>
        public static float UnitsToMinutes(float units, float unitMinutes)
        {
            return units * unitMinutes;
        }
    }
}
