using Game.Net;
using Unity.Entities;
using RailCapacityGuard.Utils;

namespace RailCapacityGuard.Services
{
    /// <summary>P2 询问结果。Confidence/Reason != 可用 时，调用方必须走"不碰"分支（不写 m_DepartureFrame）。</summary>
    public struct CapacityVerdict
    {
        public bool             Allowed;
        public Entity           Blocker;
        public uint             OccupantFreeFrame;
        public float            SlackFrames;
        public Confidence       Confidence;
        public UnavailableReason Reason;
    }

    /// <summary>
    /// P2：站台容量判断（自写实现；思路参考 TTE/RPF 的"读数据不拦函数"）。
    ///
    /// V1 实测结论（必须遵守）：
    ///   * 站台轨道（TrackLane ∧ TrackLaneFlags.Station）100% 带 LaneReservation；
    ///   * m_Blocker 语义是"挡在我前面的东西"，**不等于**"站台被谁占用"（实测 593 帧车在站台而 blocker 指向别的实体）；
    ///   * 因此判据必须是 ETA 预判：
    ///       Allowed = (blocker == null) || (blocker == 本车)
    ///                 || (etaFrames >= 占用者离开帧 - now + 余量)
    ///
    /// 硬约束：LaneReservation **不在车辆当前 lane 上** → 本接口必须接收"站台轨道实体"，
    /// 不能拿 TrainCurrentLane.m_Front.m_Lane 直接当参数（除非它本身就是 Station 轨道）。
    /// </summary>
    public sealed class PlatformCapacityService : RailGuardServiceBase
    {
        public PlatformCapacityService(World world) : base(world)
        {
        }

        public override string Name => "P2.PlatformCapacity";

        /// <summary>数据源存在性检查（站台轨道是否带 LaneReservation）。</summary>
        public bool HasReservation(EntityManager entityManager, Entity platformLane)
        {
            return platformLane != Entity.Null
                && entityManager.Exists(platformLane)
                && entityManager.HasComponent<LaneReservation>(platformLane);
        }

        public CapacityVerdict QueryArrival(
            EntityManager entityManager,
            Entity platformLane,
            Entity train,
            uint now,
            float etaFrames,
            float safetyMarginFrames)
        {
            CapacityVerdict verdict = default;

            if (!HasReservation(entityManager, platformLane))
            {
                verdict.Allowed = false;
                verdict.Confidence = Confidence.Unavailable;
                verdict.Reason = UnavailableReason.MissingComponent;
                return verdict;
            }

            LaneReservation reservation = entityManager.GetComponentData<LaneReservation>(platformLane);
            verdict.Blocker = reservation.m_Blocker;
            verdict.Confidence = Confidence.Measured;
            verdict.Reason = UnavailableReason.None;

            // 空 → 发
            if (reservation.m_Blocker == Entity.Null || reservation.m_Blocker == train)
            {
                verdict.Allowed = true;
                verdict.SlackFrames = float.MaxValue;
                return verdict;
            }

            // 被占 → 取占用者预计离开帧
            // RC-2：m_Blocker 的语义是"挡路者"，实测常指向非车辆实体；占用者不是公共交通车辆 = **不可判**。
            // 必须报 Confidence.Unavailable（调用方走"不碰"），绝不能当成"平台忙"而按住本车。
            if (!entityManager.Exists(reservation.m_Blocker)
                || !entityManager.HasComponent<Game.Vehicles.PublicTransport>(reservation.m_Blocker))
            {
                verdict.Allowed = false;
                verdict.Confidence = Confidence.Unavailable;
                verdict.Reason = UnavailableReason.NoVanillaData;
                return verdict;
            }

            Game.Vehicles.PublicTransport occupant =
                entityManager.GetComponentData<Game.Vehicles.PublicTransport>(reservation.m_Blocker);

            // 占用者是公共交通车辆，但发车帧尚未初始化（=0）→ 同样无法预判离开时间 → 不可判。
            if (occupant.m_DepartureFrame == 0u)
            {
                verdict.Allowed = false;
                verdict.Confidence = Confidence.Unavailable;
                verdict.Reason = UnavailableReason.NoVanillaData;
                return verdict;
            }

            verdict.OccupantFreeFrame = occupant.m_DepartureFrame;

            float timeToFree = (float)occupant.m_DepartureFrame - (float)now;
            if (timeToFree < 0f)
            {
                timeToFree = 0f; // 已经过了它的发车帧 → 视为随时可走
            }

            verdict.SlackFrames = etaFrames - timeToFree - safetyMarginFrames;
            verdict.Allowed = verdict.SlackFrames >= 0f;
            return verdict;
        }

        public override void ResetState()
        {
            // 无缓存状态
        }
    }
}
