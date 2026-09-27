using System.Collections.Generic;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using RailCapacityGuard.Utils;

namespace RailCapacityGuard.Services
{
    /// <summary>
    /// P4：动态寻路代价（同构 TTE PathfindCostModifierSystem 的"缓存原版值 → 应用 → 看门狗"三段式）。
    ///
    /// 对象：Game.Prefabs.PathfindTrackData —— 轨道 lane 的 **prefab 级**组件，全图仅个位数实体
    ///（dump 实证 6 个：各 TrackLaneData prefab 一份）。vanilla 关键值（dump 实测）：
    ///   SwitchCost.w=3 / DiamondCrossingCost.w=3 / TwowayCost.w=5 / CurveAngleCost={x=2,w=3} / SpawnCost.w=5。
    /// 只缩放 **Comfort 维度（float4.w）**：Switch/DiamondCrossing/Twoway 归"道岔组"，CurveAngle 归"急弯组"；
    /// Driving、CurveAngle.x、SpawnCost 等按距离/时间/生成维度的字段一律不碰（影响面不可控）。
    /// 效果：道岔与急弯越贵，寻路在平行股道间越偏向直顺走线（第五条"路径分布层：往哪走"）。
    ///
    /// 原版寻路作业按 prefab 组件现值构造成本（PathUtils.GetTrackDriveSpecification），
    /// 新发起的寻路请求立即生效；已发出的路径不受影响（车辆按事件重寻路）。
    ///
    /// 存档注意：写的是原版组件，会被原版序列化（无法挂 IPreSerialize——dump 无此接口）。
    /// 对策（TTE 同款）：PreDeserialize 恢复原版值、PostDeserialize 后由调度系统按当前设置重放；
    /// OnDispose/关闭开关时恢复。若用户卸载本 Mod，存档中残留的是最后一次应用的值（与 TTE 一致，文档已注明）。
    /// </summary>
    public sealed class PathfindCostService
    {
        // vanilla Comfort 维度常量（dump 6 实体全部一致，2026-09-27 实测）：
        // SwitchCost.w=3 / DiamondCrossingCost.w=3 / TwowayCost.w=5 / CurveAngleCost.w=3。
        // 用途①：缓存基线的完整性校验——若存档带着上次应用的缩放值回来（原版会序列化我们写的值），
        //          当前值 ≠ vanilla 常量时以常量为基线，避免"脏值当原版"越滚越偏（TTE 同款 integrity check）。
        // 用途②：读档后自愈（ResetAfterLoad）。
        private const float kVanillaSwitchW = 3f;
        private const float kVanillaDiamondW = 3f;
        private const float kVanillaTwowayW = 5f;
        private const float kVanillaCurveW = 3f;

        private struct VanillaCosts
        {
            public float SwitchW;
            public float DiamondW;
            public float TwowayW;
            public float CurveAngleW;
        }

        private readonly Dictionary<Entity, VanillaCosts> m_Vanilla = new Dictionary<Entity, VanillaCosts>(8);

        private float m_AppliedSwitch = 1f;
        private float m_AppliedCurve = 1f;
        private uint m_LastWatchdogFrame;
        private int m_LastWatchdogRewrites;

        /// <summary>缓存到的 prefab 实体数（诊断）。</summary>
        public int TargetCount => m_Vanilla.Count;

        /// <summary>上次 watchdog 是否发现并修复了漂移（诊断）。</summary>
        public int LastWatchdogRewrites => m_LastWatchdogRewrites;

        /// <summary>
        /// 应用当前倍率（首次调用时先缓存原版值）。scale=1 时不写任何数据（零风险默认）。
        /// 返回实际写入的实体数。
        /// </summary>
        public int Apply(EntityManager em, float switchScale, float curveScale)
        {
            if (switchScale <= 0f || curveScale <= 0f)
            {
                return 0;
            }

            EntityQuery query = em.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<PathfindTrackData>() });
            NativeArray<Entity> targets;
            try
            {
                targets = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            }
            catch (System.Exception ex)
            {
                ModLog.Error("[P4] query failed: " + ex);
                return 0;
            }

            using (targets)
            {
                int written = 0;
                for (int i = 0; i < targets.Length; i++)
                {
                    Entity entity = targets[i];
                    if (!em.Exists(entity) || !em.HasComponent<PathfindTrackData>(entity))
                    {
                        continue;
                    }

                    if (!m_Vanilla.TryGetValue(entity, out VanillaCosts vanilla))
                    {
                        PathfindTrackData current = em.GetComponentData<PathfindTrackData>(entity);
                        vanilla = CacheVanilla(entity, current.m_SwitchCost.m_Value.w, current.m_DiamondCrossingCost.m_Value.w,
                            current.m_TwowayCost.m_Value.w, current.m_CurveAngleCost.m_Value.w);
                        m_Vanilla[entity] = vanilla;
                    }

                    if (switchScale == 1f && curveScale == 1f)
                    {
                        continue;   // 全 1 = 不动数据（原版即所愿）
                    }

                    PathfindTrackData modified = em.GetComponentData<PathfindTrackData>(entity);
                    modified.m_SwitchCost.m_Value.w = vanilla.SwitchW * switchScale;
                    modified.m_DiamondCrossingCost.m_Value.w = vanilla.DiamondW * switchScale;
                    modified.m_TwowayCost.m_Value.w = vanilla.TwowayW * switchScale;
                    modified.m_CurveAngleCost.m_Value.w = vanilla.CurveAngleW * curveScale;
                    em.SetComponentData(entity, modified);
                    written++;
                }

                m_AppliedSwitch = switchScale;
                m_AppliedCurve = curveScale;
                ModLog.Info("[P4] applied switch=" + switchScale.ToString("F1") + " curve=" + curveScale.ToString("F1") + " targets=" + targets.Length + " written=" + written + " cached=" + m_Vanilla.Count);
                return written;
            }
        }

        /// <summary>
        /// 每 256 帧一次的对账（TTE watchdog 同款）：
        /// ① 开关/倍率变化 → 重新应用或恢复；
        /// ② 数据被外部改写（原版/其他 mod）→ 从缓存的原版值重算期望值并修复（节流日志）。
        /// </summary>
        public void Watchdog(EntityManager em, bool enabled, float switchScale, float curveScale, uint now)
        {
            m_LastWatchdogRewrites = 0;
            if (m_Vanilla.Count == 0 && (!enabled || (switchScale == 1f && curveScale == 1f)))
            {
                return;   // 从未应用过且无需应用 → 零开销
            }

            if (!enabled || (switchScale == 1f && curveScale == 1f))
            {
                if (m_AppliedSwitch != 1f || m_AppliedCurve != 1f)
                {
                    Restore(em);
                }
                return;
            }

            if (m_AppliedSwitch != switchScale || m_AppliedCurve != curveScale)
            {
                Apply(em, switchScale, curveScale);
                return;
            }

            // 漂移检查：实际值应等于 原版值×倍率
            int rewrites = 0;
            foreach (KeyValuePair<Entity, VanillaCosts> kv in m_Vanilla)
            {
                if (!em.Exists(kv.Key) || !em.HasComponent<PathfindTrackData>(kv.Key))
                {
                    continue;
                }

                PathfindTrackData current = em.GetComponentData<PathfindTrackData>(kv.Key);
                float expectSwitch = kv.Value.SwitchW * switchScale;
                float expectCurve = kv.Value.CurveAngleW * curveScale;
                if (System.Math.Abs(current.m_SwitchCost.m_Value.w - expectSwitch) > 0.001f
                    || System.Math.Abs(current.m_CurveAngleCost.m_Value.w - expectCurve) > 0.001f)
                {
                    current.m_SwitchCost.m_Value.w = kv.Value.SwitchW * switchScale;
                    current.m_DiamondCrossingCost.m_Value.w = kv.Value.DiamondW * switchScale;
                    current.m_TwowayCost.m_Value.w = kv.Value.TwowayW * switchScale;
                    current.m_CurveAngleCost.m_Value.w = kv.Value.CurveAngleW * curveScale;
                    em.SetComponentData(kv.Key, current);
                    rewrites++;
                }
            }

            if (rewrites > 0 && now - m_LastWatchdogFrame >= 1024u)
            {
                m_LastWatchdogFrame = now;
                ModLog.Warn("[P4] watchdog rewrote " + rewrites + " drifted PathfindTrackData entities");
            }
            m_LastWatchdogRewrites = rewrites;
        }

        /// <summary>
        /// 读档后自愈：实体引用跨读档失效 → 清缓存重建；
        /// 若存档带回了上次应用的缩放值（.w 偏离 vanilla 常量），先写回常量，
        /// 再由调度系统的 watchdog 按当前设置重新应用。任何模式下调用都安全（无实体即空转）。
        /// </summary>
        public void ResetAfterLoad(EntityManager em)
        {
            m_Vanilla.Clear();
            m_AppliedSwitch = 1f;
            m_AppliedCurve = 1f;
            EntityQuery query;
            NativeArray<Entity> targets;
            try
            {
                query = em.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<PathfindTrackData>() });
                targets = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            }
            catch
            {
                return;   // 菜单等无实体场景
            }

            using (targets)
            {
                int fixedDirty = 0;
                for (int i = 0; i < targets.Length; i++)
                {
                    Entity entity = targets[i];
                    if (!em.Exists(entity) || !em.HasComponent<PathfindTrackData>(entity))
                    {
                        continue;
                    }

                    PathfindTrackData current = em.GetComponentData<PathfindTrackData>(entity);
                    if (System.Math.Abs(current.m_SwitchCost.m_Value.w - kVanillaSwitchW) > 0.001f
                        || System.Math.Abs(current.m_TwowayCost.m_Value.w - kVanillaTwowayW) > 0.001f)
                    {
                        current.m_SwitchCost.m_Value.w = kVanillaSwitchW;
                        current.m_DiamondCrossingCost.m_Value.w = kVanillaDiamondW;
                        current.m_TwowayCost.m_Value.w = kVanillaTwowayW;
                        current.m_CurveAngleCost.m_Value.w = kVanillaCurveW;
                        em.SetComponentData(entity, current);
                        fixedDirty++;
                    }

                    m_Vanilla[entity] = new VanillaCosts
                    {
                        SwitchW = kVanillaSwitchW,
                        DiamondW = kVanillaDiamondW,
                        TwowayW = kVanillaTwowayW,
                        CurveAngleW = kVanillaCurveW
                    };
                }

                if (fixedDirty > 0)
                {
                    ModLog.Warn("[P4] load carried " + fixedDirty + " scaled PathfindTrackData entities -> vanilla constants restored");
                }
            }
        }

        /// <summary>
        /// 缓存原版基线：当前值与 vanilla 常量一致 → 用当前值（尊重其他 mod 的合法改写）；
        /// 不一致（= 存档脏值/外来改写）→ 以常量为基线并告警一次。
        /// </summary>
        private VanillaCosts CacheVanilla(Entity entity, float switchW, float diamondW, float twowayW, float curveW)
        {
            bool dirty = System.Math.Abs(switchW - kVanillaSwitchW) > 0.001f
                || System.Math.Abs(twowayW - kVanillaTwowayW) > 0.001f;
            if (dirty)
            {
                ModLog.Warn("[P4] entity " + entity.Index + " PathfindTrackData deviates from vanilla constants (save carried scale or foreign mod) -> using vanilla constants as baseline");
                return new VanillaCosts { SwitchW = kVanillaSwitchW, DiamondW = kVanillaDiamondW, TwowayW = kVanillaTwowayW, CurveAngleW = kVanillaCurveW };
            }

            return new VanillaCosts { SwitchW = switchW, DiamondW = diamondW, TwowayW = twowayW, CurveAngleW = curveW };
        }

        /// <summary>恢复全部原版值（关开关/卸载前）。未缓存过则无事可做。</summary>
        public void Restore(EntityManager em)
        {
            if (m_Vanilla.Count == 0)
            {
                return;
            }

            int restored = 0;
            foreach (KeyValuePair<Entity, VanillaCosts> kv in m_Vanilla)
            {
                if (!em.Exists(kv.Key) || !em.HasComponent<PathfindTrackData>(kv.Key))
                {
                    continue;
                }

                PathfindTrackData current = em.GetComponentData<PathfindTrackData>(kv.Key);
                current.m_SwitchCost.m_Value.w = kv.Value.SwitchW;
                current.m_DiamondCrossingCost.m_Value.w = kv.Value.DiamondW;
                current.m_TwowayCost.m_Value.w = kv.Value.TwowayW;
                current.m_CurveAngleCost.m_Value.w = kv.Value.CurveAngleW;
                em.SetComponentData(kv.Key, current);
                restored++;
            }

            m_AppliedSwitch = 1f;
            m_AppliedCurve = 1f;
            ModLog.Info("[P4] restored vanilla costs, restored=" + restored + "/" + m_Vanilla.Count);
        }
    }
}
