using Unity.Entities;

namespace RailCapacityGuard.Services
{
    /// <summary>
    /// 静态可变状态的统一生命周期钩子。所有持有缓存/静态状态的类型都必须实现。
    /// 游戏程序集内不存在同名类型（已核对 game_api_full.txt），无命名冲突风险。
    /// </summary>
    public interface IModResettable
    {
        void ResetState();
    }

    /// <summary>数据可信度。C 类（部分定义）数据必须带此标记。</summary>
    public enum Confidence : byte
    {
        Measured = 0,
        Derived = 1,
        Estimated = 2,
        Unavailable = 3
    }

    /// <summary>数据不可用的原因。守卫层据此决定保守拒绝还是放行。</summary>
    public enum UnavailableReason : byte
    {
        None = 0,
        MissingComponent = 1,
        MissingField = 2,
        InvalidGeometry = 3,
        NoVanillaData = 4,
        NotARailEntity = 5
    }

    /// <summary>估算值来源，用于诊断面板区分"实测"与"估算"。</summary>
    public enum EstimateSource : byte
    {
        VanillaApi = 0,
        MeshGeometry = 1,
        RuntimePathArc = 2,
        VanillaConstant = 3,
        FallbackConstant = 4
    }

    /// <summary>D13 车站分级。原版无此概念（P0-A 审计结论 D 类）。</summary>
    public enum StationClass : byte
    {
        Unknown = 0,
        Small = 1,
        Medium = 2,
        Large = 3
    }

    /// <summary>准入拒绝原因。</summary>
    public enum RejectReason : byte
    {
        None = 0,
        OccupiedByOther = 1,
        ReservedForOther = 2,
        InsufficientCapacity = 3,
        GeometryUntrusted = 4,
        ServiceUnavailable = 5,
        TrainTooLong = 6
    }

    /// <summary>
    /// 所有数据访问的唯一入口。业务代码只调 Service，不直接读游戏字段。
    /// 读失败时必须返回 false 并给出 UnavailableReason —— 不允许"读不到就放行"。
    /// </summary>
    public interface IRailGuardService : IModResettable
    {
        string Name { get; }

        /// <summary>版本守卫结果。false = 降级，相关守卫一律保守拒绝。</summary>
        bool IsAvailable { get; }

        UnavailableReason Reason { get; }
    }

    /// <summary>
    /// Service 基类：统一持有 World、统一降级标记、统一 ResetState 约定。
    /// 依赖（其他 Service）一律通过构造函数注入，不使用静态单例。
    /// </summary>
    public abstract class RailGuardServiceBase : IRailGuardService
    {
        protected RailGuardServiceBase(World world)
        {
            World = world;
        }

        protected World World { get; }

        public abstract string Name { get; }

        public bool IsAvailable { get; private set; } = true;

        public UnavailableReason Reason { get; private set; } = UnavailableReason.None;

        public abstract void ResetState();

        /// <summary>版本守卫失败或依赖缺失时调用，之后本 Service 的所有 TryGet 必须返回 false。</summary>
        protected void MarkUnavailable(UnavailableReason reason)
        {
            IsAvailable = false;
            Reason = reason;
        }

        /// <summary>版本守卫成功时调用一次。</summary>
        protected void MarkAvailable()
        {
            IsAvailable = true;
            Reason = UnavailableReason.None;
        }
    }
}
