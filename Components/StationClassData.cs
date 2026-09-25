using RailCapacityGuard.Services;
using Unity.Entities;

namespace RailCapacityGuard.Components
{
    /// <summary>
    /// D13 车站类型分级（小/中/大）。原版无此数据（P0-A 审计结论：类别 D）。
    /// 设计约束（P0-B）：
    ///   * 挂到已有车站实体（AddComponent），不新建实体；
    ///   * 从原版可读属性推导，**不进存档** —— 因此刻意不实现任何序列化接口；
    ///   * 读档后由 PostDeserialize 触发重算，ResetState() 清空。
    /// </summary>
    public struct StationClassData : IComponentData
    {
        public StationClass Class;
        public uint ComputedFrame;
    }
}
