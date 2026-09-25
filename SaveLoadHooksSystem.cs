using Colossal.Serialization.Entities;
using Game;
using Game.Serialization;
using RailCapacityGuard.Services;
using RailCapacityGuard.Utils;
using Unity.Entities;

namespace RailCapacityGuard
{
    /// <summary>
    /// IPre/PostDeserialize 的唯一落点。
    ///
    /// 硬约束：这两个接口**不会被游戏按接口扫描调用**，必须在 Mod.OnLoad 里
    /// 用 updateSystem.UpdateBefore&lt;PreDeserialize&lt;T&gt;&gt; /
    /// UpdateAfter&lt;PostDeserialize&lt;T&gt;&gt; 显式注册，漏注册等于永不运行。
    ///
    /// 本系统不做任何遍历（OnUpdate 为空），只承担生命周期钩子。
    /// </summary>
    public partial class SaveLoadHooksSystem : GameSystemBase, IPreDeserialize, IPostDeserialize
    {
        protected override void OnCreate()
        {
            base.OnCreate();

            // Mod.OnLoad 时 World 可能尚未就绪，这里用系统自身的 World 兜底初始化。
            ServiceRegistry.Current?.EnsureInitialized(World);
        }

        protected override void OnUpdate()
        {
            // 骨架：不注册任何业务逻辑，也不做全量遍历。
            //
            // 实测记录（P1 前置审计）：本系统只被注册为 Pre/PostDeserialize 的宿主，
            // 游戏的更新调度**不会**调用它的 OnUpdate；需要每帧逻辑时必须另外
            // updateSystem.UpdateAt&lt;T&gt;(phase) 注册独立系统。
        }

        public void PreDeserialize(Context context)
        {
            // 自定义数据一律"从原版推导、不进存档"：读档前先清掉全部派生缓存。
            ServiceRegistry.Current?.ResetAll();
        }

        public void PostDeserialize(Context context)
        {
            ServiceRegistry.Current?.EnsureInitialized(World);
            ModLog.Verbose("[SaveLoadHooksSystem] PostDeserialize done");
        }
    }
}
