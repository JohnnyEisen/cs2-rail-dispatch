using Game;
using Game.Simulation;
using RailCapacityGuard.Utils;
using Unity.Entities;

namespace RailCapacityGuard
{
    /// <summary>
    /// 运行时实验采集驱动（临时诊断，采集期结束即删）。
    ///
    /// 注册方式必须显式：@@updateSystem.UpdateAt<ExperimentSamplerSystem>(SystemUpdatePhase.GameSimulation)@@
    /// —— 实测（P1 前置审计）只有 UpdateAt 注册的系统才会被调度；只挂 Pre/PostDeserialize 宿主的系统
    /// 其 OnUpdate 永远不会被调用。
    ///
    /// 本系统只做一件事：每帧把 EntityManager/World 交给 ExperimentSampler。
    /// 采集逻辑、开关、输出都在 ExperimentSampler 内，主流程不感知。
    /// </summary>
    public partial class ExperimentSamplerSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            bool masterSwitchOn = Mod.Settings != null && Mod.Settings.EnableDiagnosticLogging;
            ExperimentSampler.Tick(EntityManager, World, masterSwitchOn);
        }
    }
}
