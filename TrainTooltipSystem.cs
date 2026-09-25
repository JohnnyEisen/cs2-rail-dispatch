using System;
using Game;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Game.UI.Localization;
using Game.UI.Tooltip;
using RailCapacityGuard.Runtime;
using RailCapacityGuard.Utils;
using Unity.Entities;
using TrainCurrentLane = Game.Vehicles.TrainCurrentLane;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace RailCapacityGuard
{
    /// <summary>
    /// 列车 tooltip（自写；结构参考 ExtendedTooltip 的 Systems/ExtendedTooltipSystem.cs:33/:73-90/:102-115/:124-140/:198-218，
    /// **不搬它的代码**，也不用它的 Harmony 路线）。
    ///
    /// 机制：本系统直接继承 @@Game.UI.Tooltip.TooltipSystemBase@@（dump:108083），
    /// 在 @@SystemUpdatePhase.UITooltip@@（dump:83182 = 23）里读悬停实体
    /// （@@ToolRaycastSystem.GetRaycastResult@@ dump:88751 + @@RaycastResult.m_Owner@@ dump:6759），
    /// 命中我们接管的火车时把三行 @@StringTooltip@@（dump:107803）挂到 @@TooltipGroup@@（dump:108048）并 @@AddGroup@@（dump:108086）。
    ///
    /// 数据只读：内容来自 TimetableDispatchSystem 的托管快照，本系统不新增任何 ECS 遍历。
    /// </summary>
    public partial class TrainTooltipSystem : TooltipSystemBase
    {
        private const string kGroupPath = "railCapacityGuard";

        private ToolSystem m_ToolSystem;
        private DefaultToolSystem m_DefaultTool;
        private ToolRaycastSystem m_ToolRaycastSystem;
        private TimeSystem m_Time;
        private RailTimebaseSystem m_Timebase;
        private TimetableDispatchSystem m_Dispatch;

        private TooltipGroup m_Group;

        // 缺陷 3（稳定性优先）：三个 widget 实例建一次，之后**原地改 value/color**，
        // 不再每 16 帧 Clear()+Add()，避免正在被 TooltipUISystem 引用的子项被反复摘掉。
        private StringTooltip m_LineWhen;
        private StringTooltip m_LineHeadway;
        private StringTooltip m_LineState;

        private const uint kDynamicRefreshFrames = 16;   // 缺陷 3：动态文本（剩余分钟）每 16 帧重算

        private Entity m_LastEntity;
        private uint m_LastPlanned;
        private uint m_LastTarget;
        private bool m_LastHeld;
        private string m_LastReason;
        private VehicleStateKind m_LastKind;
        private uint m_LastRefreshFrame;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_DefaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            m_ToolRaycastSystem = World.GetOrCreateSystemManaged<ToolRaycastSystem>();
            m_Time = World.GetOrCreateSystemManaged<TimeSystem>();
            m_Timebase = World.GetOrCreateSystemManaged<RailTimebaseSystem>();
            m_Dispatch = World.GetOrCreateSystemManaged<TimetableDispatchSystem>();

            m_Group = new TooltipGroup
            {
                path = kGroupPath,
                horizontalAlignment = TooltipGroup.Alignment.Start,
                verticalAlignment = TooltipGroup.Alignment.Start,
            };

            m_LineWhen = new StringTooltip();
            m_LineHeadway = new StringTooltip();
            m_LineState = new StringTooltip();
            m_Group.children.Add(m_LineWhen);
            m_Group.children.Add(m_LineHeadway);
            m_Group.children.Add(m_LineState);

            ModLog.Info("[Tooltip] TrainTooltipSystem created");
        }

        protected override void OnUpdate()
        {
            // 门控诊断（问题：上一轮日志里连一条 [Tooltip] hover= 都没有 → 说明更早就 return 了）。
            // 每 2560 帧（≈14 秒）打一条，把三个门的值都写出来，下一轮即可定位卡在哪一步。
            var settings = Mod.Settings;
            uint gateFrame = m_Timebase.CurrentFrame;
            if (gateFrame - m_LastGateLogFrame >= 2560u)
            {
                m_LastGateLogFrame = gateFrame;
                ModLog.Info("[Tooltip] gate settings=" + (settings != null ? 1 : 0)
                    + " enabled=" + (settings != null && settings.EnableTooltip ? 1 : 0)
                    + " activeToolIsDefault=" + (m_ToolSystem != null && m_ToolSystem.activeTool == m_DefaultTool ? 1 : 0)
                    + " hoverOwner=" + (m_LastHoverOwner.Index));
            }

            if (settings == null || !settings.EnableTooltip)
            {
                ResetCache();
                return;
            }

            // 只在默认工具（未选任何工具）下显示，和 ExtendedTooltip 一致。
            if (m_ToolSystem.activeTool != m_DefaultTool)
            {
                ResetCache();
                return;
            }

            RaycastResult raycast;
            if (!m_ToolRaycastSystem.GetRaycastResult(out raycast) || raycast.m_Owner == Entity.Null)
            {
                ResetCache();
                return;
            }

            Entity hovered = raycast.m_Owner;
            m_LastHoverOwner = hovered;   // 门控诊断用
            EntityManager em = EntityManager;

            // 悬停实体解析链（最多 2 跳）：自己 → Controller.m_Controller → Owner.m_Owner。
            // 依据：Game.Vehicles.Controller 唯一字段 m_Controller:Entity（dump 已核实，子实体→主控）；
            //       Game.Common.Owner.m_Owner:Entity（dump:6620，通用归属）。
            // 判据改为"**在调度器快照里有条目**"：快照只由 TimetableDispatchSystem 为被接管车辆写入，
            // 因此"有快照"本身就等价于"是主控车辆"，比"先查组件再查快照"稳（车厢/另一侧车头/不同实体编号都能命中）。
            Entity controllerOf;
            Entity ownerOf;
            VehicleTooltipInfo info;
            Entity vehicle = ResolveHoveredEntity(em, hovered, out controllerOf, out ownerOf, out info);

            LogHoverOnce(hovered, controllerOf, ownerOf, vehicle, info);

            if (vehicle == Entity.Null || !info.IsManaged)
            {
                // 解析不到 / 未接管的车辆不显示，避免误导
                ResetCache();
                return;
            }

            // 缺陷 3：静态文本（状态/间隔）只在状态变化时重建；动态文本（"约 X 分钟后"）每 16 帧强制重算。
            uint now = m_Timebase.CurrentFrame;
            bool stateChanged = vehicle != m_LastEntity
                || info.Kind != m_LastKind
                || info.PlannedFrame != m_LastPlanned
                || info.TargetFrame != m_LastTarget
                || info.Held != m_LastHeld
                || info.Reason != m_LastReason;
            bool dynamicTick = now - m_LastRefreshFrame >= kDynamicRefreshFrames;

            if (stateChanged || dynamicTick)
            {
                m_LastEntity = vehicle;
                m_LastPlanned = info.PlannedFrame;
                m_LastTarget = info.TargetFrame;
                m_LastHeld = info.Held;
                m_LastKind = info.Kind;
                m_LastReason = info.Reason;
                m_LastRefreshFrame = now;
                RebuildChildren(info);
            }

            // 退路：AddGroup 单独调用在本机不生效 → 逐行 AddMouseTooltip。
            // 参考 ExtendedTooltip/Systems/ExtendedTempTooltipSystem.cs:69/87/104/117（无 Harmony）。
            for (int i = 0; i < m_Group.children.Count; i++)
            {
                AddMouseTooltip(m_Group.children[i]);
            }
        }

        private void RebuildChildren(VehicleTooltipInfo info)
        {
            // 只更新文本，不动 children 列表（见字段注释）
            float fpm = m_Timebase.FramesPerMinute;
            if (fpm <= 0.01f)
            {
                fpm = 182.0444489f; // 262144 / 1440，vanilla @1x（dump/RailTimebaseSystem 常量）
            }

            uint target = info.TargetFrame != 0u ? info.TargetFrame : info.PlannedFrame;
            int deltaFrames = (int)target - (int)info.NowFrame;

            // 上下客中 / 图定待定：第 1 行不显示任何时刻（原本会显示"即刻"，与状态矛盾）
            if (info.Kind == VehicleStateKind.Boarding || info.Kind == VehicleStateKind.ScheduleUnknown)
            {
                m_LineWhen.value = LocalizedString.Value(info.Kind == VehicleStateKind.Boarding
                    ? "图定发车：待定（上下客中）"
                    : "图定发车：待定（站间运行未测出）");
            }
            else
            {
            // 问题一：第 1 行按状态分流 —— 只有"站台侧"状态才谈图定发车；
            // 运行中/等待中的车谈"下一站预计到达"；未参与调度的线路不谈时刻（否则会与第 3 行自相矛盾）。
            bool timetableRelevant = info.Kind == VehicleStateKind.WaitingTimetable
                || info.Kind == VehicleStateKind.HoldLimit
                || info.Kind == VehicleStateKind.WaitingPlatform
                || info.Kind == VehicleStateKind.WaitingThroat
                || info.Kind == VehicleStateKind.SegmentBusy
                || info.Kind == VehicleStateKind.HoldLimitRelease
                || info.Kind == VehicleStateKind.Releasing;

            // 问题三：ETA 太小（< 15 秒）不足以支撑"预计 HH:MM"，此时明确写"即将到达"而不是等于当前时刻
            bool etaMeaningful = info.EtaFrames >= fpm / 4f;

            if (timetableRelevant)
            {
                string when = deltaFrames <= 0
                    ? "即刻"
                    : "约 " + (deltaFrames / fpm).ToString("F1") + " 分钟后（" + ClockOf(info, target, fpm) + "）";
                m_LineWhen.value = LocalizedString.Value("图定发车：" + when);
            }
            else if (info.Kind == VehicleStateKind.Running || info.Kind == VehicleStateKind.StoppedEnRoute)
            {
                m_LineWhen.value = LocalizedString.Value(etaMeaningful
                    ? "下一站：预计 " + ClockOf(info, info.NowFrame + (uint)info.EtaFrames, fpm)
                        + "（约 " + (info.EtaFrames / fpm).ToString("F1") + " 分钟后）"
                    : "下一站：即将到达");
            }
            else
            {
                m_LineWhen.value = LocalizedString.Value("调度：本 Mod 未参与（见状态行）");
            }
            }

            // 第 2 行：站间运行时间（未知时不显示 0.0，避免"0 帧跑到下一站"的误导）
            // 下限 = 0.5 游戏分钟（与调度侧 kMinSegmentRunMinutes 一致），避免小数值被显示成 "0.0 分钟"
            m_LineHeadway.value = LocalizedString.Value(info.SegmentRunFrames >= 0.5f * fpm
                ? "站间运行：" + (info.SegmentRunFrames / fpm).ToString("F1") + " 分钟"
                : "站间运行：未测出（交回原版）");

            // 第 3 行：三层状态（国铁站台显示屏风格：待发 / 待避 / 交回原版调度）
            string state;
            TooltipColor color;
            switch (info.Kind)
            {
                case VehicleStateKind.WaitingTimetable:
                    state = "待发 · 图定 " + ClockOf(info, info.PlannedFrame, fpm) + " 开";
                    color = TooltipColor.Info;
                    break;
                case VehicleStateKind.HoldLimit:
                    // 「已等待」没有可靠的起点（我们只在站台观察），故只给上限，不编造已等待时长
                    state = "待发 · 等待接近上限（" + (info.SegmentRunFrames / fpm).ToString("F1") + " 分钟级）";
                    color = TooltipColor.Warning;
                    break;
                case VehicleStateKind.WaitingPlatform:
                    state = "待避 · 前方站台占用，预计 " + ClockOf(info, target, fpm) + " 开";
                    color = TooltipColor.Warning;
                    break;
                case VehicleStateKind.WaitingThroat:
                    state = "待避 · 咽喉区占用";
                    color = TooltipColor.Warning;
                    break;
                case VehicleStateKind.SegmentBusy:
                    state = "待避 · 前方区间占用";
                    color = TooltipColor.Warning;
                    break;
                case VehicleStateKind.HoldLimitRelease:
                    state = "放行 · 等待超限（强制）";
                    color = TooltipColor.Warning;
                    break;
                case VehicleStateKind.Releasing:
                    state = "放行 · 立即发车";
                    color = TooltipColor.Success;
                    break;
                case VehicleStateKind.DataUnavailable:
                    // 问题三：只有"真读不到站台/ETA/占用者"才叫数据不可用
                    state = "数据不可用（交回原版）";
                    color = TooltipColor.Warning;
                    break;
                case VehicleStateKind.Boarding:
                    state = "上下客中（等待，原版处理）";
                    color = TooltipColor.Info;
                    break;
                case VehicleStateKind.ScheduleUnknown:
                    state = "未参与调度 · 站间运行未测出（交回原版）";
                    color = TooltipColor.Info;
                    break;
                case VehicleStateKind.ForcedDepart:
                    state = "强制发车 · 停站超时（原版上下客卡死兜底）";
                    color = TooltipColor.Warning;
                    break;
                case VehicleStateKind.StoppedEnRoute:
                    // 问题二：停了就说停住，并给出我们能看到的原因（速度来自 TrainNavigation.m_Speed）
                    state = "等待中 · 速度 " + info.Speed.ToString("F1") + " m/s"
                        + (string.IsNullOrEmpty(info.Reason) ? string.Empty : " " + StopCause(info.Reason));
                    color = TooltipColor.Warning;
                    break;
                default:
                    // 问题一：ETA 已挪到第 1 行，这里只报"在跑"
                    state = info.Speed >= 0f
                        ? "运行中 · 速度 " + info.Speed.ToString("F1") + " m/s"
                        : "运行中";
                    color = TooltipColor.Info;
                    break;
            }

            m_LineState.value = LocalizedString.Value("状态：" + state);
            m_LineState.color = color;
        }

        /// <summary>问题二：从 dispatch 的 reason 里取出停住原因（形如"（前方车辆 123 占用）"）。</summary>
        private static string StopCause(string reason)
        {
            int open = reason.IndexOf('（');
            return open >= 0 ? reason.Substring(open) : string.Empty;
        }

        /// <summary>把某个帧换算成当日时刻 HH:MM（用 TimeSystem.normalizedTime，dump:77241）。</summary>
        private string ClockOf(VehicleTooltipInfo info, uint frame, float fpm)
        {
            double dayFraction = m_Time.normalizedTime
                + ((double)frame - (double)info.NowFrame) / fpm / 1440.0;
            dayFraction -= Math.Floor(dayFraction);

            int totalMinutes = (int)(dayFraction * 1440.0);
            if (totalMinutes < 0)
            {
                totalMinutes = 0;
            }

            return (totalMinutes / 60).ToString("00") + ":" + (totalMinutes % 60).ToString("00");
        }

        /// <summary>把内部 reason 文本转成给玩家看的一句话。</summary>
        private static string Describe(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return "未知原因";
            }

            if (reason.StartsWith("[1]"))
            {
                return "早到，等图定时刻";
            }

            if (reason.StartsWith("[2]"))
            {
                return "目标站台被占";
            }

            if (reason.StartsWith("[3]"))
            {
                return "咽喉区被占";
            }

            return reason;
        }

        private const int kMaxHoverCandidates = 8;

        private Entity m_LastLoggedHover;   // 诊断日志节流：每个悬停实体只打一条
        private Entity m_LastHoverOwner;    // 最近一次射线命中（门控诊断用）
        private uint m_LastGateLogFrame;

        /// <summary>
        /// 依次尝试：悬停实体本身 → Controller.m_Controller → Owner.m_Owner，以及它们的
        /// Controller/Owner 一跳（最多 2 跳、最多 8 个候选，全部 O(1) 组件读取）。
        /// 返回第一个"在调度器快照里"的候选；若都没有快照，则退一步返回第一个"像主控车辆"
        /// （有 PublicTransport + TrainCurrentLane）的候选，供上层判为未接管并打日志。
        /// </summary>
        private Entity ResolveHoveredEntity(EntityManager em, Entity hovered, out Entity controllerOf, out Entity ownerOf, out VehicleTooltipInfo info)
        {
            controllerOf = Entity.Null;
            ownerOf = Entity.Null;
            info = default(VehicleTooltipInfo);

            if (!em.Exists(hovered))
            {
                return Entity.Null;
            }

            controllerOf = ReadController(em, hovered);
            ownerOf = ReadOwner(em, hovered);

            Entity[] candidates = new Entity[kMaxHoverCandidates];
            int count = 0;
            AddCandidate(candidates, ref count, hovered);
            AddCandidate(candidates, ref count, controllerOf);
            AddCandidate(candidates, ref count, ownerOf);
            AddCandidate(candidates, ref count, ReadController(em, controllerOf));
            AddCandidate(candidates, ref count, ReadOwner(em, controllerOf));
            AddCandidate(candidates, ref count, ReadController(em, ownerOf));
            AddCandidate(candidates, ref count, ReadOwner(em, ownerOf));

            for (int i = 0; i < count; i++)
            {
                if (m_Dispatch.TryGetVehicleInfo(candidates[i], out info) && info.IsManaged)
                {
                    return candidates[i];
                }
            }

            for (int i = 0; i < count; i++)
            {
                Entity candidate = candidates[i];
                if (em.Exists(candidate)
                    && em.HasComponent<VehiclePublicTransport>(candidate)
                    && em.HasComponent<TrainCurrentLane>(candidate))
                {
                    return candidate;
                }
            }

            return Entity.Null;
        }

        private static Entity ReadController(EntityManager em, Entity entity)
        {
            if (entity == Entity.Null || !em.Exists(entity) || !em.HasComponent<Game.Vehicles.Controller>(entity))
            {
                return Entity.Null;
            }

            return em.GetComponentData<Game.Vehicles.Controller>(entity).m_Controller;
        }

        private static Entity ReadOwner(EntityManager em, Entity entity)
        {
            if (entity == Entity.Null || !em.Exists(entity) || !em.HasComponent<Game.Common.Owner>(entity))
            {
                return Entity.Null;
            }

            return em.GetComponentData<Game.Common.Owner>(entity).m_Owner;
        }

        private static void AddCandidate(Entity[] candidates, ref int count, Entity entity)
        {
            if (entity == Entity.Null || count >= candidates.Length)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                if (candidates[i] == entity)
                {
                    return;
                }
            }

            candidates[count++] = entity;
        }

        /// <summary>悬停诊断：每个悬停实体打一条（Info 级，玩家没开 Verbose 也能看到）。</summary>
        private void LogHoverOnce(Entity hovered, Entity controllerOf, Entity ownerOf, Entity resolved, VehicleTooltipInfo info)
        {
            if (hovered == m_LastLoggedHover)
            {
                return;
            }

            m_LastLoggedHover = hovered;
            ModLog.Info("[Tooltip] hover=" + hovered.Index
                + " controller=" + controllerOf.Index
                + " owner=" + ownerOf.Index
                + " resolved=" + resolved.Index
                + " managed=" + (info.IsManaged ? 1 : 0)
                + " kind=" + info.Kind);
        }

        /// <summary>
        /// 只重置"缓存键"，**绝不清空 m_Group.children**。
        /// 回归根因：三个 widget 是 OnCreate 里建一次并加入 children 的固定实例；
        /// 旧版 ResetCache() 会 children.Clear()，而未命中帧本来就会频繁调用它 →
        /// 第一次 Clear 之后本会话内再也不会重新 Add，于是三行永久不显示。
        /// </summary>
        private void ResetCache()
        {
            m_LastEntity = Entity.Null;
            m_LastReason = null;
        }
    }
}
