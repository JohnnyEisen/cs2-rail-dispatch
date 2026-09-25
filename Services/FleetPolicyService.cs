using Unity.Entities;
using RailCapacityGuard.Utils;

namespace RailCapacityGuard.Services
{
    /// <summary>
    /// 车队双向自适应（D）。机制参考 Transit Timetables（MIT）的
    /// HourlyFleetSystem.TrySetLineFleet + RouteModifier[VehicleInterval] 注入路径；
    /// 公式以**原版**为准（dump:78639 max(1, round(lineDuration / max(1, vehicleInterval)))）。
    ///
    /// ⚠ 单位假设（**待运行时验证 V10**）：本实现把"线路时长(loopMinutes)"与
    ///   TransportLine.m_VehicleInterval 都当作**游戏内分钟**处理，写出的 delta 也是分钟差值。
    ///   原版 CalculateVehicleInterval(lineDuration, vehicleCount) 的 lineDuration 单位我们在
    ///   1.6.2f1 上未反编译确认 → 先按分钟实现，并把每次写入值记入日志，便于标定。
    ///
    /// 竞态：RefreshRouteModifiers 会清空后重建 RouteModifier buffer（归档结论）→
    ///   本服务每个评估周期都重写一次（TT 的做法），并在卸载时回写零 delta。
    /// </summary>
    public sealed class FleetPolicyService : RailGuardServiceBase
    {
        private readonly StationResolverService m_Resolver;

        public FleetPolicyService(World world, StationResolverService resolver) : base(world)
        {
            m_Resolver = resolver;
        }

        public override string Name => "D.FleetPolicy";

        /// <summary>原版公式（dump:78639）：max(1, round(loop / max(1, interval)))。</summary>
        public static int ComputeVanillaTargetCount(float loopMinutes, float intervalMinutes)
        {
            float safeInterval = intervalMinutes > 1f ? intervalMinutes : 1f;
            int count = (int)System.Math.Round((double)(loopMinutes / safeInterval));
            return count < 1 ? 1 : count;
        }

        /// <summary>线路圈时（分钟）。来源：终点站 waypoint 的 VehicleTiming.m_AverageTravelTime（帧）÷ 每秒帧数。</summary>
        public float GetLoopMinutes(EntityManager entityManager, Entity line, float framesPerMinute)
        {
            if (framesPerMinute <= 0.01f)
            {
                return 0f;
            }

            Entity terminus;
            if (!m_Resolver.TryGetTerminusWaypoint(entityManager, line, out terminus)
                || !entityManager.Exists(terminus)
                || !entityManager.HasComponent<Game.Routes.VehicleTiming>(terminus))
            {
                return 0f;
            }

            float frames = entityManager.GetComponentData<Game.Routes.VehicleTiming>(terminus).m_AverageTravelTime;
            return frames / framesPerMinute;
        }

        /// <summary>
        /// 把线路车队规模写到 RouteModifier[VehicleInterval]（等同玩家拖滑块；车辆仍由原版从调度场生成/退役）。
        /// 返回 false 时 reason 给出原因，调用方不重试本 tick。
        /// </summary>
        public bool TrySetFleet(
            EntityManager entityManager,
            Entity line,
            int target,
            float defaultIntervalMinutes,
            float loopMinutes,
            out float appliedDelta,
            out string reason)
        {
            appliedDelta = 0f;
            reason = null;

            if (target < 1)
            {
                reason = "target<1";
                return false;
            }

            if (loopMinutes <= 0.01f)
            {
                reason = "loopMinutes unavailable";
                return false;
            }

            if (!entityManager.Exists(line)
                || !entityManager.HasComponent<Game.Routes.TransportLine>(line)
                || !entityManager.HasBuffer<Game.Routes.RouteModifier>(line))
            {
                reason = "missing TransportLine/RouteModifier";
                return false;
            }

            float desiredInterval = loopMinutes / target;
            float delta = desiredInterval - defaultIntervalMinutes;

            DynamicBuffer<Game.Routes.RouteModifier> modifiers =
                entityManager.GetBuffer<Game.Routes.RouteModifier>(line);
            int index = (int)Game.Routes.RouteModifierType.VehicleInterval;
            while (modifiers.Length <= index)
            {
                modifiers.Add(default(Game.Routes.RouteModifier));
            }

            Game.Routes.RouteModifier modifier = modifiers[index];
            if (System.Math.Abs(modifier.m_Delta.x - delta) < 0.001f)
            {
                reason = "no change";
                return false;
            }

            modifier.m_Delta.x = delta;
            modifiers[index] = modifier;
            appliedDelta = delta;

            ModLog.Verbose("[Fleet] line=" + line.Index + " target=" + target
                + " loop=" + loopMinutes.ToString("F2") + "min interval=" + desiredInterval.ToString("F2")
                + "min delta=" + delta.ToString("F3"));
            return true;
        }

        /// <summary>卸载/关闭时把我们写过的 delta 清零（避免残留到存档）。</summary>
        public void ClearFleet(EntityManager entityManager, Entity line)
        {
            if (!entityManager.Exists(line) || !entityManager.HasBuffer<Game.Routes.RouteModifier>(line))
            {
                return;
            }

            DynamicBuffer<Game.Routes.RouteModifier> modifiers =
                entityManager.GetBuffer<Game.Routes.RouteModifier>(line);
            int index = (int)Game.Routes.RouteModifierType.VehicleInterval;
            if (modifiers.Length <= index)
            {
                return;
            }

            Game.Routes.RouteModifier modifier = modifiers[index];
            if (modifier.m_Delta.x != 0f)
            {
                modifier.m_Delta.x = 0f;
                modifiers[index] = modifier;
                ModLog.Verbose("[Fleet] cleared VehicleInterval delta on line " + line.Index);
            }
        }

        public override void ResetState()
        {
            // 无缓存状态（写入的 delta 由原版重建流程覆盖；本服务不做持久化）
        }
    }
}
