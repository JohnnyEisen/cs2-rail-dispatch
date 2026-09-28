using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Game.Vehicles;
using RailCapacityGuard.Runtime;
using RailCapacityGuard.Services;
using RailCapacityGuard.Utils;
using Unity.Collections;
using Unity.Entities;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace RailCapacityGuard
{

public class TimetableDispatchSystem : GameSystemBase
{
	private const int MaxVehiclesPerLine = 32;

	private const int MaxLinesPerTick = 64;

	private const uint FleetPassInterval = 256u;

	private RailTimebaseSystem m_Timebase;

	private EntityQuery m_LineQuery;

	private readonly Dictionary<Entity, LineRuntimeState> m_States = new Dictionary<Entity, LineRuntimeState>();

	private PlatformCapacityService m_Capacity;

	private ThroatZoneService m_Throat;

	private StationResolverService m_Resolver;

	private FleetPolicyService m_Fleet;

	private bool m_Running;

	private bool m_PendingResolve;

	private bool m_CleanupPending;

	private bool m_CleanedThisLoad;

	private uint m_LastStatsFrame;

	private int m_WindowWrites;

	private int m_WindowNoData;

	private int m_WindowHolds;

	private int m_WindowDeparts;

	// 仪表（2026-09-27"tooltip 消失"排查）：seen = 通过 IsManagedVehicle 的车次；recorded = 快照写入次数
	private int m_WindowSeen;

	private int m_WindowRecorded;

	private uint m_LastZoneRebuild;

	// 国铁对齐常量已归拢至 Runtime/CnRailwayTuning.cs（kZoneStaggerFrames/kPunctualFrames/kDwellCompression）
	private int m_WindowOnTime;

	private int m_WindowLate;
	private readonly Dictionary<int, uint> m_ZoneLastRelease = new Dictionary<int, uint>(32);
	private readonly Dictionary<int, Entity> m_ZoneLastLine = new Dictionary<int, Entity>(32);

	private uint m_LastPathfindWatchdog;

	private readonly Dictionary<Entity, byte> m_LastDecision = new Dictionary<Entity, byte>(256);

	private readonly SegmentTimeService m_SegmentTime = new SegmentTimeService();

	// P4 动态寻路代价（默认关闭；开启时缓存原版值→应用→watchdog，读档/卸载恢复）
	private readonly PathfindCostService m_PathfindCost = new PathfindCostService();

	private readonly HashSet<Entity> m_FirstEntryLogged = new HashSet<Entity>();

	private readonly Dictionary<Entity, float> m_LastHeadwayLogged = new Dictionary<Entity, float>(64);

	private readonly HashSet<Entity> m_TerminusLogged = new HashSet<Entity>();


	private readonly HashSet<Entity> m_SpeedProbeLogged = new HashSet<Entity>();   // P8：限速探针首次写入日志去重

	private readonly HashSet<Entity> m_SegmentUnknownLogged = new HashSet<Entity>();

	private readonly Dictionary<Entity, VehicleSchedule> m_VehicleSchedule = new Dictionary<Entity, VehicleSchedule>(256);

	private readonly Dictionary<Entity, VehicleTooltipInfo> m_TooltipInfo = new Dictionary<Entity, VehicleTooltipInfo>(256);

	// 主循环行缓存（避免每 8 帧分配 NativeArray）
	private readonly List<Entity> m_CachedLines = new List<Entity>(64);

	// 重设线路时原版"删旧建新"可同帧完成 → 行数不变 → 缓存不刷新，残留失效实体（NRE@GetComponentData）
	private readonly List<Entity> m_StaleLines = new List<Entity>(8);
	private int m_CachedLineCount = -1;
	private uint m_LastTooltipPrune;
	private readonly List<Entity> m_PruneScratch = new List<Entity>(64);
	private readonly HashSet<Entity> m_TreatAsFreeLogged = new HashSet<Entity>();

	private const float kStoppedSpeedThreshold = 0.5f;

	private const float kDefaultHoldCapFrames = 3600f;

	private const float kDefaultBoardingCapMinutes = 180f;

	private const float kMinEtaUnits = 0.5f;

	private const float kMinSegmentRunMinutes = 0.5f;

	private const int MaxSegmentCheck = 8;

	private const int MaxUnknownBlockerHolds = 8;

	private readonly Dictionary<Entity, int> m_UnknownBlockerHolds = new Dictionary<Entity, int>(256);

	public int LastLineCount { get; private set; }

	public int LastWriteCount { get; private set; }

	public int LastNoDataCount { get; private set; }

	public override int GetUpdateInterval(SystemUpdatePhase phase)
	{
		return 8;
	}

	protected override void OnCreate()
	{
		base.OnCreate();
		m_Timebase = ((ComponentSystemBase)this).World.GetOrCreateSystemManaged<RailTimebaseSystem>();
		m_LineQuery = GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
			.WithAll<Route, TransportLine, RouteWaypoint, PrefabRef>()
			.WithNone<Deleted, Temp>());
	}

	protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
	{
		base.OnGameLoadingComplete(purpose, mode);
		bool flag = (m_Running = (int)mode == 2 && ((int)purpose == 1 || (int)purpose == 2));
		m_PendingResolve = true;
		m_CleanupPending = flag;
		m_CleanedThisLoad = false;
		m_WindowWrites = 0;
		m_WindowNoData = 0;
		m_WindowHolds = 0;
		m_WindowDeparts = 0;
		if (!flag)
		{
			ClearPerLoadState();
		}
		else
		{
			try
			{
				// P4：实体引用跨读档失效 → 清缓存；存档若带回缩放值先写回原版常量（之后按设置重放）
				m_PathfindCost.ResetAfterLoad(EntityManager);
			}
			catch (Exception)
			{
			}
		}
		ModLog.Info("[P7] OnGameLoadingComplete running=" + m_Running + " mode=" + mode.ToString() + " purpose=" + purpose.ToString());
	}

	protected override void OnUpdate()
	{
		TransitTimetablesFree();
	}

	private void TransitTimetablesFree()
	{
		RailCapacityGuardSetting settings = Mod.Settings;
		if (settings == null)
		{
			return;
		}
		ServiceRegistry current = ServiceRegistry.Current;
		if (current == null || !current.IsInitialized || (m_Capacity == null && (!current.TryGetService<PlatformCapacityService>(out m_Capacity) || !current.TryGetService<ThroatZoneService>(out m_Throat) || !current.TryGetService<StationResolverService>(out m_Resolver) || !current.TryGetService<FleetPolicyService>(out m_Fleet))))
		{
			return;
		}
		uint currentFrame = m_Timebase.CurrentFrame;
		float framesPerMinute = m_Timebase.FramesPerMinute;
		float unitMinutes = m_Timebase.UnitMinutes;
		EntityManager entityManager = ((ComponentSystemBase)this).EntityManager;
		int num = m_LineQuery.CalculateEntityCount();
		if (!m_Running && num <= 0)
		{
			return;
		}
		if (m_PendingResolve)
		{
			m_PendingResolve = false;
			ClearPerLoadState();
			ModLog.Verbose("[P7] per-load state cleared");
		}
		if (m_CleanupPending)
		{
			m_CleanupPending = false;
			CleanFleetPollution(entityManager);
		}
		if (currentFrame - m_LastStatsFrame >= 256)
		{
			m_LastStatsFrame = currentFrame;
			ModLog.Verbose("[P7] window: writes=" + m_WindowWrites + " noData=" + m_WindowNoData + " holds=" + m_WindowHolds + " departs=" + m_WindowDeparts + " seen=" + m_WindowSeen + " recorded=" + m_WindowRecorded + " onTime=" + m_WindowOnTime + " late=" + m_WindowLate);
			m_WindowWrites = 0;
			m_WindowNoData = 0;
			m_WindowHolds = 0;
			m_WindowDeparts = 0;
			m_WindowSeen = 0;
			m_WindowRecorded = 0;
			m_WindowOnTime = 0;
			m_WindowLate = 0;
		}
		if (settings.EnableThroatCoordination)
		{
			uint num2 = (uint)Math.Max(1024, settings.DiagnosticIntervalFrames);
			if (currentFrame - m_LastZoneRebuild >= num2)
			{
				m_Throat.Rebuild(entityManager, currentFrame);
				m_LastZoneRebuild = currentFrame;
			}
		}
		// P4 watchdog（256 帧对账，TTE 同构）：倍率变更→重应用；数据漂移→从原版基线修复；关闭→恢复
		if (currentFrame - m_LastPathfindWatchdog >= 256u)
		{
			m_LastPathfindWatchdog = currentFrame;
			try
			{
				m_PathfindCost.Watchdog(entityManager, settings.EnablePathfindCostScale, settings.PathfindSwitchCostScale, settings.PathfindCurveCostScale, currentFrame);
			}
			catch (Exception ex)
			{
				ModLog.Error("[P4] watchdog failed: " + ex);
			}
		}
		if (!settings.EnableTimetableDispatch)
		{
			return;
		}
		// 行数不变 ⇒ 复用缓存，避免每 8 帧一次 Temp 分配（行数变化时才重读一次 query）
		int lineCountNow = m_LineQuery.CalculateEntityCount();
		if (lineCountNow != m_CachedLineCount)
		{
			m_CachedLines.Clear();
			NativeArray<Entity> fresh = m_LineQuery.ToEntityArray(Allocator.Temp);
			try
			{
				for (int i = 0; i < fresh.Length; i++)
				{
					m_CachedLines.Add(fresh[i]);
				}
			}
			finally
			{
				fresh.Dispose();
			}

			m_CachedLineCount = lineCountNow;
		}

		// 每 4096 帧剪一次已消失车辆的 tooltip 快照
		if (currentFrame - m_LastTooltipPrune >= 4096u)
		{
			m_LastTooltipPrune = currentFrame;
			m_PruneScratch.Clear();
			foreach (Entity key in m_TooltipInfo.Keys)
			{
				if (!entityManager.Exists(key))
				{
					m_PruneScratch.Add(key);
				}
			}
			for (int k = 0; k < m_PruneScratch.Count; k++)
			{
				m_TooltipInfo.Remove(m_PruneScratch[k]);
			}
		}

		int num3 = (LastLineCount = Math.Min(m_CachedLines.Count, 64));
		LastWriteCount = 0;
		LastNoDataCount = 0;
		for (int i = 0; i < num3; i++)
		{
			ProcessLine(entityManager, settings, m_CachedLines[i], currentFrame, framesPerMinute, unitMinutes);
		}
		if (m_StaleLines.Count > 0)
		{
			for (int i = 0; i < m_StaleLines.Count; i++)
			{
				Entity stale = m_StaleLines[i];
				m_CachedLines.Remove(stale);
				m_States.Remove(stale);
				m_LastHeadwayLogged.Remove(stale);
				m_SegmentUnknownLogged.Remove(stale);
				m_TerminusLogged.Remove(stale);
				m_FirstEntryLogged.Remove(stale);
			}
			m_StaleLines.Clear();
			LastLineCount = Math.Min(m_CachedLines.Count, 64);
		}
	}

	private void ProcessLine(EntityManager em, RailCapacityGuardSetting s, Entity line, uint now, float fpm, float unitMinutes)
	{
		if (line == Entity.Null || !em.Exists(line) || !em.HasComponent<TransportLine>(line))
		{
			// 失效线路实体（重设线路后缓存未刷新）：标记待剪枝，绝不能裸 GetComponentData
			//（原型里已无该组件 → ChunkDataUtility NRE，整个模拟帧报错）
			m_StaleLines.Add(line);
			return;
		}
		if (!m_States.TryGetValue(line, out var value))
		{
			value = new LineRuntimeState
			{
				Line = line,
				Initialized = true
			};
		}
		value.DataUnavailable = false;
		value.CapacityBlocked = false;
		TransportLine componentData = em.GetComponentData<TransportLine>(line);
		ExportTimetable(em, s, line, now, fpm);
		float num = ((componentData.m_VehicleInterval > 0.01f) ? componentData.m_VehicleInterval : 5f);
		float num2 = UnitConversion.UnitsToMinutes(num, unitMinutes);
		// 站间运行时间：主源 = 本线实测 leg 中位数（ring，只装真实 leg）；回退 = 线路段 PathInformation 中位数。
		// 车辆自身 PathInformation.m_Duration 已证伪（寻路完成时写入的整条路径静态快照），读取已全删。
		float value3 = m_SegmentTime.GetLineMedian(line);
		int value2 = (value3 > 0f) ? 1 : 0;
		value.SegmentRunFrames = value3;
		value.MinHeadwayFrames = Math.Max(8f, s.MinHeadwayMinutes * fpm);
		if (value3 <= 0f && m_SegmentUnknownLogged.Add(line))
		{
			ModLog.Info("[P7] line=" + line.Index + " segment run unknown: leg=none(该车本 tick 无上一段样本) lineEma=" + value2 + " samples(<" + 2 + " 或 <" + (0.5f * fpm).ToString("F0") + "f) fallback=segment median(需 ≥" + 0.5f + " unit) -> 本站图定待定，交回原版");
		}
		value.MaxEarlyFrames = value3 * ((float)s.MaxEarlyPercent / 100f);
		if (!m_LastHeadwayLogged.TryGetValue(line, out var value4) || Math.Abs(value4 - value3) > 0.5f)
		{
			m_LastHeadwayLogged[line] = value3;
			ModLog.Verbose("[P7] line=" + line.Index + " segmentRunFrames=" + value3.ToString("F0") + " samples=" + value2 + " vanillaIntervalUnits=" + num.ToString("F2") + " (仅诊断，不参与决策)");
		}
		if (!em.HasBuffer<RouteVehicle>(line))
		{
			value.DataUnavailable = true;
			m_States[line] = value;
			return;
		}
		DynamicBuffer<RouteVehicle> buffer = em.GetBuffer<RouteVehicle>(line, true);
		int num3 = Math.Min(buffer.Length, 32);
		if (num3 == 0)
		{
			m_States[line] = value;
			return;
		}
		bool flag = value.LastWrittenDepartureFrame == 0;
		uint num4 = now;
		if (m_TerminusLogged.Add(line) && em.HasBuffer<RouteWaypoint>(line))
		{
			DynamicBuffer<RouteWaypoint> buffer2 = em.GetBuffer<RouteWaypoint>(line, true);
			if (buffer2.Length > 0)
			{
				m_Resolver.TryGetOriginWaypoint(em, line, out var waypoint);
				m_Resolver.TryGetTerminusWaypoint(em, line, out var waypoint2);
				ModLog.Verbose("[P7] line=" + line.Index + " waypoints=" + buffer2.Length + " firstWp=" + buffer2[0].m_Waypoint.Index + " lastWp=" + buffer2[buffer2.Length - 1].m_Waypoint.Index + " origin=" + waypoint.Index + " terminus=" + waypoint2.Index + " (问题四：站序按 Waypoint.m_Index 判定，不再靠 buffer 顺序)");
			}
		}
		if (flag && m_FirstEntryLogged.Add(line))
		{
			ModLog.Verbose("[P7] first-entry planned=now line=" + line.Index);
		}
		for (int i = 0; i < num3; i++)
		{
			Entity vehicle = buffer[i].m_Vehicle;
			if (!IsManagedVehicle(em, vehicle))
			{
				continue;
			}
			m_WindowSeen++;
			VehiclePublicTransport componentData2 = em.GetComponentData<VehiclePublicTransport>(vehicle);
			if (m_Resolver.TryGetCurrentStationLane(em, vehicle, out var _) && (componentData2.m_State & PublicTransportFlags.AbandonRoute) != 0 && (componentData2.m_State & PublicTransportFlags.Boarding) == 0)
			{
				ref PublicTransportFlags state = ref componentData2.m_State;
				state = (PublicTransportFlags)((uint)state & 0xFFFFFDFFu);
				em.SetComponentData<VehiclePublicTransport>(vehicle, componentData2);
				ModLog.Verbose("[P7] stripped AbandonRoute (at platform) vehicle=" + vehicle.Index);
			}
			float num5 = (em.HasComponent<TrainNavigation>(vehicle) ? em.GetComponentData<TrainNavigation>(vehicle).m_Speed : (-1f));
			// P8-试验性：限速写入探针（默认关）。原版语义（反编译 Game.Vehicles.Blocker.Deserialize 证实）：
			// m_MaxSpeed 为 byte，byte/5 = m/s，clamp 0–255（=183.6 km/h）。原版持续维护该字段——
			// 关闭探针即交回原版，无需手动恢复；开启时本 Mod 接管全部管理车辆的允许速度。
			if (s.EnableSpeedControlProbe && s.SpeedControlProbeKmh > 0f && em.HasComponent<Blocker>(vehicle))
			{
				int probeRaw = Math.Min(255, Math.Max(0, (int)(s.SpeedControlProbeKmh / 3.6f * 5f + 0.5f)));
				Blocker probeBlocker = em.GetComponentData<Blocker>(vehicle);
				if (probeBlocker.m_MaxSpeed != (byte)probeRaw)
				{
					probeBlocker.m_MaxSpeed = (byte)probeRaw;
					em.SetComponentData(vehicle, probeBlocker);
					LastWriteCount++;
					if (m_SpeedProbeLogged.Add(vehicle))
					{
						ModLog.Info("[P8] speed probe vehicle=" + vehicle.Index + " maxSpeed=" + probeRaw + " (" + s.SpeedControlProbeKmh.ToString("F0") + " km/h)");
					}
				}
			}
			bool flag2 = (componentData2.m_State & PublicTransportFlags.Boarding) != 0
				|| IsBoardingAtLineStop(em, line, vehicle)
				|| m_Resolver.TryGetCurrentStationLane(em, vehicle, out var _);   // 原版权威信号优先（TransportBoardingHelpers: BeginBoarding 设 BoardingVehicle / EndBoarding 清）
			if (!m_VehicleSchedule.TryGetValue(vehicle, out var value5))
			{
				value5 = default(VehicleSchedule);
			}
			uint num9;
			if (flag2)   // 在站台
			{
				value5.NotAtStopTicks = 0;
				if (!value5.AtStop)
				{
					float num6 = 0.5f * fpm;

					// 真实 leg 入库：只要知道本段真实起点就记（ring 是唯一可信来源，不能被估计有效性/自检挡住）
					if (value5.LegStartFrame != 0u && now > value5.LegStartFrame)
					{
						float numActual = (float)(now - value5.LegStartFrame);
						// 护栏：入库/留存的实测 leg 一律夹取（>1 游戏日 = 回绕/跨日停摆垃圾；leg=17895760 事故）
						float numActualClamped = SegmentTimeService.ClampLegFrames(numActual, fpm);
						m_SegmentTime.RecordLeg(line, numActualClamped);
						value5.LastLegFrames = numActualClamped;   // 本车自己的实测 leg（下一段优先用它 → 图定不因线路中位数波动而跳）

						// 自检只负责告警（估计 vs 实际）
						if (value5.LegEstimateFrames > 0f)
						{
							float numRatio = (numActual > 0f) ? (value5.LegEstimateFrames / numActual) : 0f;
							if (numRatio < 0.5f || numRatio > 2.0f)
							{
								ModLog.Warn("[P7] leg sanity vehicle=" + vehicle.Index + " estimate=" + value5.LegEstimateFrames.ToString("F0") + " actual=" + numActual.ToString("F0") + " ratio=" + numRatio.ToString("F2"));
								value5.LegEstimateOk = false;
							}
						}
					}
					else if (value5.LegStartFrame == 0u)
					{
						// B 诊断：进站转换时无 leg 起点 ⇒ 两个 legStart setter（boarding 写入段 / 主写入段）都未命中过本车
						ModLog.Verbose("[P7] legSkip vehicle=" + vehicle.Index + " line=" + line.Index + " reason=LegStartFrame=0 (进站转换但无 leg 起点)");
					}

					// 本段完成 → 允许下次发车重新读一次估计
					value5.LegStartFrame = 0u;

					// 本段估计：车辆自身 PathInformation.m_Duration 已弃用（反编译证实＝寻路完成时写入的
					// “整条路径静态快照”，既不是剩余时长也不是本段）。改为：
					//   ① 本线真实 leg 中位数（ring 只装真实 leg）  ② 线路段 PathInformation 中位数
					//     （均值已被 D3 实测否证：折返/绕路等离群段把 Σ÷n 抬高 3–10×）
					// ① 本车上一段实测 leg（首选：同一列车自己的时间尺度，最稳）
					float num7 = (value5.LastLegFrames >= num6) ? value5.LastLegFrames : 0f;
					// 本车上一段实测 vs 线路中位数离谱偏离守卫（2026-09-27 现象3：107 条 leg sanity，
					// 短腿 320f 被复用到 2896f 长段）：偏离 2.5×/0.4× 以上时改信中位数（需 ≥3 样本），
					// 避免上一段工况（短驳/被堵）污染本段图定。
					if (num7 >= num6 && m_SegmentTime.GetLineSampleCount(line) >= 3)
					{
						float numMedianGuard = m_SegmentTime.GetLineMedian(line);
						if (numMedianGuard >= num6 && (num7 > numMedianGuard * 2.5f || num7 < numMedianGuard * 0.4f))
						{
							num7 = numMedianGuard;
						}
					}
					// ② 线路真实 leg 中位数（需 ≥3 样本，1–2 个样本的中位数会随每一段跳变）
					if (num7 < num6 && m_SegmentTime.GetLineSampleCount(line) >= 3)
					{
						num7 = m_SegmentTime.GetLineMedian(line);
					}
					// ③ 原版 VehicleTiming.m_AverageTravelTime 中位数（每站 BeginBoarding 都在刷新；
					//   存的是 route units，服务层内部转帧；区间外样本=uint 回绕垃圾，已被服务层丢弃）
					if (num7 < num6)
					{
						float numVt;
						if (m_SegmentTime.TryGetLineVehicleTimingMedian(em, line, unitMinutes, fpm, num6, out numVt))
						{
							num7 = numVt;
						}
					}
					// ④ 线路段 PathInformation 的中位数（最后兜底）
					if (num7 < num6)
					{
						float numMedian;
						if (m_SegmentTime.TryGetLineMedianLegFrames(em, line, unitMinutes, fpm, num6, now, out numMedian))
						{
							num7 = numMedian;
						}
					}
					value5.LegEstimateFrames = ((num7 >= num6) ? num7 : 0f);
					value5.LegEstimateOk = (value5.LegEstimateFrames > 0f);

					uint num8 = ((value5.LastDepartFrame != 0) ? value5.LastDepartFrame : now);
					value5.ScheduleUnknown = num7 < num6;
					// 国铁补丁④（算法测算）：晚点恢复 = 压缩停站。上一段晚点的车，本站计划停站按
					// CnRailwayTuning.kDwellCompression 压缩（下限 0.25 游戏分钟），图定随之上调回落——否则链式图定
					// （上一站实际 + 区间 + 停站）会把晚点永久传播、越积越深。实测放行仍由时刻表
					// 锚定（Hold 至 planned），与"不早开"不冲突：planned 压缩后更早，是编图目标提前。
					float dwellForPlan = value5.LastDwellFrames;
					if (value5.LateFrames > CnRailwayTuning.kPunctualFrames && dwellForPlan > 0f)
					{
						float compressed = Math.Max(0.25f * fpm, dwellForPlan * CnRailwayTuning.kDwellCompression);
						ModLog.Verbose("[P7] dwell compression vehicle=" + vehicle.Index + " late=" + value5.LateFrames.ToString("F0") + "f dwell " + dwellForPlan.ToString("F0") + "->" + compressed.ToString("F0"));
						dwellForPlan = compressed;
					}
					value5.PlannedDepartFrame = (value5.ScheduleUnknown ? now : (num8 + (uint)(num7 + dwellForPlan)));
					value5.AtStop = true;
					value5.StopEnterFrame = now;
					ModLog.Verbose("[P7] legSrc line=" + line.Index + " ring=" + m_SegmentTime.GetLineSampleCount(line) + " median=" + m_SegmentTime.GetLineMedian(line).ToString("F0") + " used=" + num7.ToString("F0"));
					ModLog.Verbose("[P7] schedule locked vehicle=" + vehicle.Index + " base=" + num8 + " leg=" + num7.ToString("F0") + " dwell=" + value5.LastDwellFrames.ToString("F0") + " planned=" + value5.PlannedDepartFrame);
				}
				num9 = value5.PlannedDepartFrame;
			}
			else if (value5.AtStop && num5 >= 0.5f)
			{
				// 权威在站信号（Boarding ∥ BoardingVehicle）已断 **且车在动** = 真的走了。
				// 不再要求 planned 已过：车头一离站 boarding 就结束，若此时 planned 还在未来，
				// 旧逻辑会落进 8 tick 闩锁 → tooltip 先“未知”再弹回“图定”，车尾离站再弹一次。
				value5.LastDepartFrame = now;
				// 国铁补丁②③（2026-09-27）：晚点量 = 实际离开 − 图定发车；正点 ≤ 0.5 游戏分钟。
				// 晚点量入库供"晚点车优先恢复正点"；计数进 window 正点率 KPI。
				if (value5.PlannedDepartFrame != 0u)
				{
					value5.LateFrames = (float)now - (float)value5.PlannedDepartFrame;
					if (value5.LateFrames <= CnRailwayTuning.kPunctualFrames)
					{
						m_WindowOnTime++;
					}
					else
					{
						m_WindowLate++;
					}
					// 基准表正点率累积（tooltip 第 5 行数据源）
					if (m_Baselines.TryGetValue(line, out var blForOnTime))
					{
						if (value5.LateFrames <= CnRailwayTuning.kPunctualFrames)
						{
							blForOnTime.OnTimeCount++;
						}
						else
						{
							blForOnTime.LateCount++;
						}
						m_Baselines[line] = blForOnTime;
					}
				}
				if (value5.StopEnterFrame != 0u && now > value5.StopEnterFrame)
				{
					value5.LastDwellFrames = Math.Min((float)(now - value5.StopEnterFrame), 1800f);
					m_SegmentTime.RecordDwell(line, value5.LastDwellFrames);   // P8 基准时刻表数据源
				}
				if (value5.LegStartFrame == 0u)
				{
					value5.LegStartFrame = now;   // 本段实时测算起点（顺带消灭 legSkip）
					AnchorLegProgress(em, vehicle, ref value5, now);   // 第二十一条：位置乘数锚定
				}
				value5.AtStop = false;
				value5.NotAtStopTicks = 0;
				num9 = now;
			}
			else if (value5.AtStop && num5 < 0.5f && ++value5.NotAtStopTicks < 8)
			{
				// 闩锁：连续 8 个 tick（64 帧）都拿不到站台才解除，避免速度抖动导致重锁/图定“即刻”闪烁
				num9 = value5.PlannedDepartFrame;
			}
			else
			{
				value5.AtStop = false;
				value5.NotAtStopTicks = 0;
				num9 = now;
			}
			// 第二十一条：区间位置测定（位置乘数）——运行中的车每 tick 读一次 PathOwner/PathElement/navLen
			if (!flag2)
			{
				UpdateLegProgress(em, vehicle, ref value5, now);
			}

			// 本段剩余帧（空间层 ETA / tooltip）：① (1−Progress)×本段估计 ② 本段估计 − 已跑 ③ 线路真实 leg 中位数 ④ VehicleTiming ⑤ 线路段中位数
			float legEta = -1f;
			if (value5.LegProgressValid && value5.LegProgress >= 0f && value5.LegEstimateOk && value5.LegEstimateFrames >= 0.5f * fpm)
			{
				legEta = (1f - value5.LegProgress) * value5.LegEstimateFrames;   // 位置乘数（第二十一条）
			}
			if (legEta < 0f && value5.LegEstimateOk && value5.LegEstimateFrames >= 0.5f * fpm)
			{
				float legElapsed = (value5.LegStartFrame != 0u && now > value5.LegStartFrame) ? (float)(now - value5.LegStartFrame) : 0f;
				legEta = value5.LegEstimateFrames - legElapsed;
				if (legEta < 0f)
				{
					legEta = 0f;
				}
			}
			if (legEta < 0f && value3 >= 0.5f * fpm)
			{
				legEta = value3;   // 线路真实 leg 中位数兜底
			}
			// ③ 原版 per-waypoint 平均行程（VehicleTiming.m_AverageTravelTime，每站刷新；units→帧、回绕垃圾已在服务层过滤）
			if (legEta < 0f)
			{
				float numVtEta;
				if (m_SegmentTime.TryGetLineVehicleTimingMedian(em, line, unitMinutes, fpm, 0.5f * fpm, out numVtEta))
				{
					legEta = numVtEta;
				}
			}
			// ④ 线路段中位数（最后兜底）
			if (legEta < 0f)
			{
				float numMedianEta;
				if (m_SegmentTime.TryGetLineMedianLegFrames(em, line, unitMinutes, fpm, 0.5f * fpm, now, out numMedianEta))
				{
					legEta = numMedianEta;   // 线路段中位数兜底
				}
			}
			if (legEta > SegmentTimeService.kMaxLegFrames)
			{
				legEta = SegmentTimeService.kMaxLegFrames;   // 护栏底线：任何 leg ETA 不超过 1 游戏日
			}

			// tooltip 第 2 行数据源：本车本段估计（没读到则用线路中位数）
			value.SegmentRunFrames = (value5.LegEstimateFrames > 0f) ? value5.LegEstimateFrames : value3;

			// 运行中分流（2026-09-27 修订）：所有"不在站台、不在上客"的车都不进发车决策链。
			// 修复前两个病灶：① 站台不在导航窗内 → NoData"未知"（前修）；② 站台在窗内且图定已过
			// → Decide[9] 对还在跑的车返回"放行·立即发车/即刻"（22:01 会话 [9] release eta=0/656f 实证）。
			// 发车决策只在站台上有意义；行驶车只报 Running/StoppedEnRoute + 本段 ETA。
			if (!value5.AtStop && (componentData2.m_State & PublicTransportFlags.Boarding) == 0)
			{
				VehicleStateKind midKind = ((num5 >= 0f && num5 < 0.5f) ? VehicleStateKind.StoppedEnRoute : VehicleStateKind.Running);
				string midReason = ((midKind == VehicleStateKind.StoppedEnRoute)
					? "[2x] mid-segment stopped speed=" + num5.ToString("F2") + " m/s" + DescribeStopCause(em, vehicle)
					: "[2x] running to next stop, departure decision N/A");
				RecordTooltipInfo(line, vehicle, value, num9, num9, now, DepartureDecision.NoData, midKind, legEta, num5, midReason);
				m_VehicleSchedule[vehicle] = value5;
				continue;
			}

			DepartureDecision departureDecision = Decide(em, s, buffer, num3, line, vehicle, componentData2, num, value, now, fpm, unitMinutes, legEta, num9, out var target, out var early, out var bypassSlotFloor, out var kind, out var etaOut, out var reason);
			if (departureDecision == DepartureDecision.NoData && kind == VehicleStateKind.Running && num5 >= 0f && num5 < 0.5f)
			{
				kind = VehicleStateKind.StoppedEnRoute;
				reason = "[2] stopped en-route speed=" + num5.ToString("F2") + " m/s" + DescribeStopCause(em, vehicle);
			}
			LogDecisionOnce(line, vehicle, departureDecision, now, num9, target, reason);
			if ((componentData2.m_State & PublicTransportFlags.Boarding) != 0)
			{
				float num10 = ((s.MaxBoardingMinutes > 0f) ? s.MaxBoardingMinutes : 180f) * fpm;
				uint num11 = ((value5.AtStop && value5.StopEnterFrame != 0 && now > value5.StopEnterFrame) ? (now - value5.StopEnterFrame) : 0u);
				if (value5.AtStop && num11 > (uint)num10)
				{
					uint num12 = ReleaseFrame(now);
					if (componentData2.m_DepartureFrame != num12)
					{
						componentData2.m_DepartureFrame = num12;
						em.SetComponentData<VehiclePublicTransport>(vehicle, componentData2);
						LastWriteCount++;
						m_WindowWrites++;
					}
					value5.LastDwellFrames = num11;
					m_SegmentTime.RecordDwell(line, num11);   // 强制发车也计入停站统计（P8）
					value5.LastDepartFrame = num12;   // 强制发车也算一次真实发车
					if (!value5.ForcedDepartLogged)
					{
						value5.ForcedDepartLogged = true;
						ModLog.Warn("[P7] forced depart (boarding timeout) vehicle=" + vehicle.Index + " waited=" + num11 + " frames");
					}
					m_VehicleSchedule[vehicle] = value5;
					RecordTooltipInfo(line, vehicle, value, num9, num12, now, DepartureDecision.Depart, VehicleStateKind.ForcedDepart, etaOut, num5, "[6] boarding timeout dwell=" + num11 + "f > cap " + num10.ToString("F0") + "f");
				}
				else
				{
					// A: boarding 期间照常执行时刻表写入（TT TimetableDispatchSystem.cs:1728-1740：
					// vanilla StopBoarding 只在 boarding 期间读 m_DepartureFrame，且 StartBoarding 每 tick
					// 会膨胀该字段。此前 continue 跳过写入 ⇒ Hold 不生效、vanilla 早于 planned 放行、
					// at-stop Depart 不可达 ⇒ LegStartFrame 恒 0 ⇒ ring 断供）。
					if (departureDecision == DepartureDecision.NoData && value5.AtStop && !value5.ScheduleUnknown)
					{
						departureDecision = ((now < num9) ? DepartureDecision.Hold : DepartureDecision.Depart);
					}
					if (departureDecision == DepartureDecision.Hold || departureDecision == DepartureDecision.Depart)
					{
						uint writeFrameB;
						if (departureDecision == DepartureDecision.Hold)
						{
							uint num12b = Math.Max(num9, now + (uint)s.PostponeStepFrames);
							if (target < num12b)
							{
								target = num12b;
							}
							writeFrameB = target;
						}
						else
						{
							uint num12b = (uint)Math.Min(1800f, ((s.MaxBoardingMinutes > 0f) ? s.MaxBoardingMinutes : 180f) * fpm);
							long num13b = (long)target + (long)num12b - 1800L;
							writeFrameB = ((num13b > 1L) ? (uint)num13b : 1u);
							if (writeFrameB > now)
							{
								writeFrameB = now;
							}
						}
						bool needWriteB = ((departureDecision == DepartureDecision.Hold)
							? (componentData2.m_DepartureFrame != writeFrameB)
							: (componentData2.m_DepartureFrame > writeFrameB));   // release 只下调，绝不上调（TT:1777）
						if (needWriteB)
						{
							componentData2.m_DepartureFrame = writeFrameB;
							em.SetComponentData<VehiclePublicTransport>(vehicle, componentData2);
							LastWriteCount++;
							m_WindowWrites++;
						}
						if (departureDecision == DepartureDecision.Depart && value5.LegStartFrame == 0u)
						{
							// B 诊断：boarding 期放行是 at-stop Depart 的主要命中点
							value5.LegStartFrame = now;
							AnchorLegProgress(em, vehicle, ref value5, now);
							ModLog.Verbose("[P7] legStart vehicle=" + vehicle.Index + " line=" + line.Index + " frame=" + now + " src=boarding-depart");
						}
					}
					m_VehicleSchedule[vehicle] = value5;
					RecordTooltipInfo(line, vehicle, value, num9, target, now, departureDecision, VehicleStateKind.Boarding, etaOut, num5, "[0] boarding write (" + departureDecision + ") dwell=" + num11 + "f (" + reason + ")", num11, num10);
				}
				continue;
			}
			if (value5.ScheduleUnknown)
			{
				m_VehicleSchedule[vehicle] = value5;
				RecordTooltipInfo(line, vehicle, value, num9, target, now, DepartureDecision.NoData, VehicleStateKind.ScheduleUnknown, etaOut, num5, "[1] segment run unknown -> 待定/交回原版");
				continue;
			}
			// 站点内以时刻表为准：空间层偶尔拿不到前方站台时，不能把已锁定的车站判定清成 运行中/原版接管
			if (departureDecision == DepartureDecision.NoData && value5.AtStop && !value5.ScheduleUnknown)
			{
				if (now < num9)
				{
					departureDecision = DepartureDecision.Hold;
					kind = VehicleStateKind.WaitingTimetable;
				}
				else
				{
					departureDecision = DepartureDecision.Depart;
					kind = VehicleStateKind.Releasing;
				}
				reason = "[1] at stop, timetable authoritative -> " + departureDecision.ToString();
			}

			switch (departureDecision)
			{
			case DepartureDecision.NoData:
				LastNoDataCount++;
				m_WindowNoData++;
				value.DataUnavailable = true;
				m_VehicleSchedule[vehicle] = value5;
				RecordTooltipInfo(line, vehicle, value, num9, target, now, departureDecision, kind, etaOut, num5, reason);
				continue;
			case DepartureDecision.Hold:
			{
				value.HoldCount++;
				m_WindowHolds++;
				value.PostponeStreak++;
				value.CapacityBlocked = true;
				uint num13 = Math.Max(num9, now + (uint)s.PostponeStepFrames);
				if (target < num13)
				{
					target = num13;
				}
				break;
			}
			}
			if (departureDecision == DepartureDecision.Depart)
			{
				value.PostponeStreak = 0;
				m_WindowDeparts++;
				// 阶段 4 余项：经咽喉 zone 的放行记录（错峰判据的写入侧）
				if (s.EnableThroatCoordination && m_Resolver.TryGetUpcomingStationLane(em, vehicle, out var releaseLane)
					&& m_Throat.TryGetZoneId(releaseLane, out int releaseZone))
				{
					m_ZoneLastRelease[releaseZone] = now;
					m_ZoneLastLine[releaseZone] = line;
				}
				if (bypassSlotFloor)
				{
					if (target > now)
					{
						target = now;
					}
				}
				else
				{
					if (target < num4)
					{
						target = num4;
					}
					num4 = target + (uint)value.MinHeadwayFrames;
				}
			}
			if (departureDecision != DepartureDecision.Depart && departureDecision != DepartureDecision.Hold)
			{
				m_VehicleSchedule[vehicle] = value5;
				continue;
			}
			RecordTooltipInfo(line, vehicle, value, num9, target, now, departureDecision, kind, etaOut, num5, reason);
			// ── 写 m_DepartureFrame：照搬 TT 现行语义（MIT；TT TimetableDispatchSystem.cs:1733 / :1770-1777）──
			//   hold   ：每 tick 权威重写 target —— 原版 StartBoarding 每 tick 会膨胀该字段，不重写就被原版放走（TT:20-21、:588）
			//   release：只「下调」到锚点 force = planned + maxDwell − 1800（且 ≤ now），
			//            把原版 StopBoarding 的放弃点（frame >= m_DepartureFrame + 1800）锚定在「图定 + 最大停站」上：
			//            · v0.2 的 frame-1 会保留两个登车守卫无上限 → 越走越晚（TT:1749-1751 已回滚）
			//            · v0.2.3 的 frame-1800 会立刻清空守卫 → 把还在走来的乘客丢回站台（TT:1752-1755 已回滚）
			uint maxDwellFrames = (uint)Math.Min(1800f, (s.MaxBoardingMinutes > 0f ? s.MaxBoardingMinutes : 180f) * fpm);
			uint writeFrame;
			if (departureDecision == DepartureDecision.Hold)
			{
				writeFrame = target;
			}
			else
			{
				long anchor = (long)target + (long)maxDwellFrames - 1800L;
				writeFrame = anchor > 1L ? (uint)anchor : 1u;
				if (writeFrame > now)
				{
					writeFrame = now;
				}
			}
			bool needWrite = (departureDecision == DepartureDecision.Hold)
				? (componentData2.m_DepartureFrame != writeFrame)
				: (componentData2.m_DepartureFrame > writeFrame);   // release 只下调，绝不上调（TT:1777）
			if (needWrite)
			{
				componentData2.m_DepartureFrame = writeFrame;
				em.SetComponentData<VehiclePublicTransport>(vehicle, componentData2);
				LastWriteCount++;
				m_WindowWrites++;
			}
			if (departureDecision == DepartureDecision.Depart && value5.LegStartFrame == 0u)
			{
				// 只记本段起点（真实发车时刻）；估计值来自线路级来源，不再读车辆 m_Duration
				value5.LegStartFrame = now;
				AnchorLegProgress(em, vehicle, ref value5, now);
				ModLog.Verbose("[P7] legStart vehicle=" + vehicle.Index + " line=" + line.Index + " frame=" + now + " src=main-depart");
			}
			
			if (departureDecision == DepartureDecision.Depart)
			{
				value.LastWrittenDepartureFrame = now;

				// 实际离开帧与闩锁解除统一在“观察到真的走了”那一支处理（上方 latch 分支），避免重复记录

				if (early)
				{
					value.EarlyCount++;
				}
			}

			// 状态持久化：本 tick 对时刻表的修改必须写回（AtStop / LegEstimate* / LastDwell*）
			m_VehicleSchedule[vehicle] = value5;
		}
		int num14 = 450;
		if (value.PostponeStreak > num14)
		{
			ModLog.Warn("[P7] line " + line.Index + " held " + value.PostponeStreak + " ticks (>= " + num14 + ") → 已达绝对上限，交由原版节奏");
			value.PostponeStreak = 0;
		}
		m_States[line] = value;
	}

	private DepartureDecision Decide(EntityManager em, RailCapacityGuardSetting s, DynamicBuffer<RouteVehicle> vehicles, int vehicleCount, Entity line, Entity vehicle, VehiclePublicTransport pt, float intervalUnits, LineRuntimeState state, uint now, float fpm, float unitMinutes, float legEtaFrames, uint planned, out uint target, out bool early, out bool bypassSlotFloor, out VehicleStateKind kind, out float etaOut, out string reason)
	{
		target = planned;
		early = false;
		bypassSlotFloor = false;
		kind = VehicleStateKind.Running;
		etaOut = ((legEtaFrames >= 0f) ? legEtaFrames : -1f);   // NoData 返回也携带 ETA（第 1 行不再"未知"）
		reason = null;
		if (now < planned)
		{
			if (!m_Resolver.TryGetCurrentStationLane(em, vehicle, out var _) && (pt.m_State & PublicTransportFlags.Boarding) == 0)
			{
				kind = VehicleStateKind.Running;
				reason = "[1] now<planned but not at a platform -> NoData (running)";
				return DepartureDecision.NoData;
			}
			float num = ((s.MaxHoldMinutes > 0f) ? (s.MaxHoldMinutes * fpm) : 3600f);
			if ((float)(planned - now) > num)
			{
				target = ReleaseFrame(now);
				bypassSlotFloor = true;
				early = true;
				kind = VehicleStateKind.HoldLimitRelease;
				reason = "[1] wait " + (planned - now) + "f > cap " + num.ToString("F0") + "f -> release (target=now-1800)";
				return DepartureDecision.Depart;
			}
			kind = ((!((float)(planned - now) > num * 0.8f)) ? VehicleStateKind.WaitingTimetable : VehicleStateKind.HoldLimit);
			reason = "[1] early hold (at platform) planned=" + planned + " wait=" + (planned - now) + "f cap=" + num.ToString("F0") + "f";
			return DepartureDecision.Hold;
		}
		if (!s.EnableCapacityFeedback)
		{
			reason = "[2] capacity feedback disabled -> depart";
			return DepartureDecision.Depart;
		}
		if (!m_Resolver.TryGetUpcomingStationLane(em, vehicle, out var lane2))
		{
			if (m_Resolver.TryGetCurrentStationLane(em, vehicle, out var _) || (pt.m_State & PublicTransportFlags.Boarding) != 0)
			{
				target = planned;   // 图定时刻本身；由写入侧锚定 maxDwell（见 TT:1770-1776）
				bypassSlotFloor = true;
				kind = VehicleStateKind.Releasing;
				reason = "[2] at stop, no upcoming platform -> depart (anchored)";
				return DepartureDecision.Depart;
			}
			kind = VehicleStateKind.Running;
			reason = "[2] no upcoming station lane -> NoData (running)";
			return DepartureDecision.NoData;
		}
		if (!m_Capacity.HasReservation(em, lane2))
		{
			kind = VehicleStateKind.DataUnavailable;
			reason = "[2] platform " + lane2.Index + " has no LaneReservation -> NoData";
			return DepartureDecision.NoData;
		}
		// 空间层 ETA：已知用真实剩余帧；未知 → 按 0 处理（保守=立即到站：站台忙就待避），
		// 但显示仍报 -1（“下一站：未知”）。绝不再因为一个数字缺失就退出整个空间层。
		float num2 = (legEtaFrames >= 0f) ? legEtaFrames : 0f;
		etaOut = (legEtaFrames >= 0f) ? legEtaFrames : -1f;
		string source = "leg";
		CapacityVerdict capacityVerdict = m_Capacity.QueryArrival(em, lane2, vehicle, now, num2, s.SafetyMarginFrames);
		if (capacityVerdict.Confidence == Confidence.Unavailable)
		{
			// 判定不可用（常因占用者不是公交车辆/离开帧未知）⇒ 视为空闲继续走后面的检查，
			// 而不是整辆车退出空间层（旧行为 = P2/P3/区间全部跳过，实测 noData 占大头）
			if (m_TreatAsFreeLogged.Add(lane2))   // 每个站台每会话只记一条（实测一轮刷了 1106 条）
			{
				ModLog.Verbose("[P7] verdict unavailable (" + capacityVerdict.Reason.ToString() + ") platform=" + lane2.Index + " -> treat as free");
			}
			capacityVerdict.Blocker = Entity.Null;
		}
		if (IsUpcomingSegmentBusy(em, vehicle, num2, now))
		{
			kind = VehicleStateKind.SegmentBusy;
			reason = "[1x] segment busy -> Hold eta=" + num2.ToString("F0");
			return DepartureDecision.Hold;
		}
		// 阶段 2 公式化（国铁追踪间隔 I追，2026-09-27）：后车与前车离开**同一车站**的时间差
		// ≥ I追（= MinHeadwayFrames）方可发出，即 CN 运行图"同方向追踪发车间隔"。
		// 替代旧 progress<0.6 magic number——I追 随设置自适应，elapsed=now−前车 LegStartFrame；
		// 前车起点未测到（=0）不判（第二十五条）。前车区间停车时 elapsed 照算，超窗后交原版闭塞兜底。
		uint trackingHeadway = (uint)Math.Max(8f, s.MinHeadwayMinutes * fpm);
		for (int j = 0; j < vehicleCount; j++)
		{
			Entity frontVehicle = vehicles[j].m_Vehicle;
			if (frontVehicle == vehicle || !IsManagedVehicle(em, frontVehicle))
			{
				continue;
			}

			VehicleSchedule frontSched;
			if (!m_VehicleSchedule.TryGetValue(frontVehicle, out frontSched) || frontSched.AtStop
				|| frontSched.LegStartFrame == 0u)
			{
				continue;
			}

			float elapsed = (float)(now - frontSched.LegStartFrame);
			if (elapsed >= (float)trackingHeadway)
			{
				continue;
			}

			kind = VehicleStateKind.SegmentBusy;
			reason = "[5] tracking headway: front train " + frontVehicle.Index + " departed " + elapsed.ToString("F0") + "f ago < I=" + trackingHeadway + "f -> Hold";
			return DepartureDecision.Hold;
		}
		// 阶段 3 已移除（2026-09-27 实测无效）：PathTargets 数据源是站端车道，[7] 全场 0 触发；
		// 跨线共享冲突由 [3]（zone 占用/错峰）与 [5]（前车位置）覆盖。
		if (!(capacityVerdict.Blocker == Entity.Null) && !(capacityVerdict.Blocker == vehicle))
		{
			float num3 = capacityVerdict.OccupantFreeFrame;
			float num4 = num3 - num2;
			float num5 = (float)now - num4;
			if (num5 < 0f - state.MaxEarlyFrames)
			{
				kind = VehicleStateKind.WaitingPlatform;
				reason = "[9] too early for slot desired=" + num4.ToString("F0") + " dev=" + num5.ToString("F0") + "f eta=" + num2.ToString("F0") + " occFree=" + num3.ToString("F0") + " source=" + source + " -> Hold";
				return DepartureDecision.Hold;
			}
			bool flag = num5 >= (float)s.SafetyMarginFrames;
			bool flag2 = HasRearPressure(em, vehicles, vehicleCount, vehicle, lane2) && state.PostponeStreak * 8 >= s.PostponeStepFrames;
			if (flag | flag2)
			{
				target = planned;
				bypassSlotFloor = true;
				kind = VehicleStateKind.Releasing;
				reason = "[9] release " + (flag ? "(slot passed)" : "(rear pressure at A)") + " dev=" + num5.ToString("F0") + "f eta=" + num2.ToString("F0") + " occFree=" + num3.ToString("F0") + " slack=" + capacityVerdict.SlackFrames.ToString("F0") + " source=" + source;
				return DepartureDecision.Depart;
			}
			kind = VehicleStateKind.WaitingPlatform;
			reason = "[9] slot hold dev=" + num5.ToString("F0") + "f eta=" + num2.ToString("F0") + " occFree=" + num3.ToString("F0") + " slack=" + capacityVerdict.SlackFrames.ToString("F0") + " source=" + source + " -> Hold";
			return DepartureDecision.Hold;
		}
		kind = VehicleStateKind.Releasing;
		reason = "[9] platform free eta=" + num2.ToString("F0") + " source=" + source + " -> depart";
		if (s.EnableThroatCoordination && m_Throat.TryGetZoneId(lane2, out var zoneId))
		{
			// 阶段 4 余项（2026-09-27）：zone 错峰——他线在 MinHeadwayFrames 内刚经此 zone 放行过
			// → 本车推迟（同线车不受限，线内节奏由 [1x]/[5]/slot 链管）。
			uint zoneReleasedAt = 0u;
			Entity zoneReleasedBy = Entity.Null;
			bool wouldStagger = m_ZoneLastRelease.TryGetValue(zoneId, out zoneReleasedAt) && now - zoneReleasedAt < (uint)CnRailwayTuning.kZoneStaggerFrames
				&& m_ZoneLastLine.TryGetValue(zoneId, out zoneReleasedBy) && zoneReleasedBy != line;
			// 国铁补丁②：晚点车优先恢复正点——本车晚点超过阈值时不受他线错峰窗限制
			bool iAmLate = m_VehicleSchedule.TryGetValue(vehicle, out var mySched) && mySched.LateFrames > CnRailwayTuning.kPunctualFrames;
			if (wouldStagger)
			{
				if (iAmLate)
				{
					ModLog.Verbose("[P7] late priority vehicle=" + vehicle.Index + " late=" + mySched.LateFrames.ToString("F0") + "f -> skip zone stagger");
				}
				else
				{
					kind = VehicleStateKind.WaitingThroat;
					reason = "[3] zone " + zoneId + " stagger: line " + zoneReleasedBy.Index + " released " + (now - zoneReleasedAt) + "f ago -> Hold";
					return DepartureDecision.Hold;
				}
			}
			if (m_Throat.TryGetStandingBlocker(em, zoneId, vehicle, out var throatBlocker))
			{
				// 阶段 4 轻量版（2026-09-27）：停驻挡路车若是管理车辆且自身 ETA 可读，
				// 剩余 < 安全余量 = 即将腾出 → 不按住（对照区间判据：能算出它什么时候走才做提前量判断）。
				float throatFreeIn = -1f;
				if (m_TooltipInfo.TryGetValue(throatBlocker, out var throatSnap) && throatSnap.IsManaged && throatSnap.EtaFrames >= 0f)
				{
					throatFreeIn = throatSnap.EtaFrames;
				}
				if (throatFreeIn < 0f || throatFreeIn >= (float)s.SafetyMarginFrames)
				{
					kind = VehicleStateKind.WaitingThroat;
					reason = "[3] throat zone " + zoneId + " busy blocker=" + throatBlocker.Index
						+ (throatFreeIn >= 0f ? " freeIn=" + throatFreeIn.ToString("F0") : " freeIn=?") + " -> Hold";
					return DepartureDecision.Hold;
				}
				ModLog.Verbose("[P3] zone " + zoneId + " blocker=" + throatBlocker.Index + " clearing in " + throatFreeIn.ToString("F0") + "f -> treat as free");
			}
		}
		if (s.EnableEarlyDeparture && state.MaxEarlyFrames >= 1f)
		{
			uint num6 = (uint)Math.Max(now, (float)planned - state.MaxEarlyFrames);
			if (now >= num6 && HasRearPressure(em, vehicles, vehicleCount, vehicle, lane2))
			{
				target = num6;   // 提前发车时刻
				early = true;
				bypassSlotFloor = true;
				kind = VehicleStateKind.Releasing;
				reason = "[4] rear pressure -> early depart (planned=" + planned + ")";
				return DepartureDecision.Depart;
			}
		}
		target = planned;   // 正常到点放行：写图定时刻，由写入侧锚定 maxDwell（TT:1770-1776）
		bypassSlotFloor = true;
		kind = VehicleStateKind.Releasing;
		reason = "[5] depart (planned=" + planned + ")";
		return DepartureDecision.Depart;
	}

	public bool TryGetVehicleInfo(Entity vehicle, out VehicleTooltipInfo info)
	{
		return m_TooltipInfo.TryGetValue(vehicle, out info);
	}

	private void RecordTooltipInfo(Entity line, Entity vehicle, LineRuntimeState state, uint planned, uint target, uint now, DepartureDecision decision, VehicleStateKind kind, float etaFrames, float speed, string reason, float dwellFrames = -1f, float boardingCapFrames = -1f)
	{
		m_WindowRecorded++;
		if (m_TooltipInfo.Count > 1024)
		{
			m_TooltipInfo.Clear();
		}
		m_TooltipInfo[vehicle] = new VehicleTooltipInfo
		{
			IsManaged = true,
			Kind = kind,
			Line = line,
			PlannedFrame = planned,
			TargetFrame = target,
			NowFrame = now,
			SegmentRunFrames = state.SegmentRunFrames,
			EtaFrames = etaFrames,
			Speed = speed,
			DwellFrames = dwellFrames,
			BoardingCapFrames = boardingCapFrames,
			Held = (decision == DepartureDecision.Hold),
			Reason = reason
		};
	}

	private void ClearTooltipInfo()
	{
		m_TooltipInfo.Clear();
	}

	private void LogDecisionOnce(Entity line, Entity vehicle, DepartureDecision decision, uint now, uint planned, uint target, string reason)
	{
		if (ModLog.VerboseEnabled)
		{
			if (m_LastDecision.Count > 1024)
			{
				m_LastDecision.Clear();
			}
			byte b = (byte)decision;
			if (!m_LastDecision.TryGetValue(vehicle, out var value) || value != b)
			{
				m_LastDecision[vehicle] = b;
				ModLog.Verbose("[P7] vehicle=" + vehicle.Index + " line=" + line.Index + " decision=" + decision.ToString() + " now=" + now + " planned=" + planned + " target=" + target + " | " + reason);
			}
		}
	}

	private bool HasRearPressure(EntityManager em, DynamicBuffer<RouteVehicle> vehicles, int vehicleCount, Entity self, Entity myPlatform)
	{
		if (myPlatform == Entity.Null)
		{
			return false;
		}
		for (int i = 0; i < vehicleCount; i++)
		{
			Entity vehicle = vehicles[i].m_Vehicle;
			if (!(vehicle == self) && IsManagedVehicle(em, vehicle) && m_Resolver.TryGetUpcomingStationLane(em, vehicle, out var lane) && lane == myPlatform)
			{
				return true;
			}
		}
		return false;
	}



	private string DescribeStopCause(EntityManager em, Entity vehicle)
	{
		if (!em.HasComponent<TrainCurrentLane>(vehicle))
		{
			return "（原因未知）";
		}
		Entity lane = em.GetComponentData<TrainCurrentLane>(vehicle).m_Front.m_Lane;
		if (lane != Entity.Null && em.Exists(lane) && em.HasComponent<LaneReservation>(lane))
		{
			Entity blocker = em.GetComponentData<LaneReservation>(lane).m_Blocker;
			if (blocker != Entity.Null && blocker != vehicle)
			{
				return "（前方车辆 " + blocker.Index + " 占用）";
			}
		}
		return "（信号/闭塞等待）";
	}

	private bool IsUpcomingSegmentBusy(EntityManager em, Entity vehicle, float etaFrames, uint now)
	{
		if (etaFrames <= 0f || !em.HasComponent<TrainNavigationLane>(vehicle))
		{
			return false;
		}
		DynamicBuffer<TrainNavigationLane> buffer = em.GetBuffer<TrainNavigationLane>(vehicle, true);
		int num = Math.Min(buffer.Length, 8);
		if (num <= 1)
		{
			return false;
		}
		for (int i = 0; i < num; i++)
		{
			Entity lane = buffer[i].m_Lane;
			if (lane == Entity.Null || !em.Exists(lane) || !em.HasComponent<LaneReservation>(lane))
			{
				continue;
			}
			Entity blocker = em.GetComponentData<LaneReservation>(lane).m_Blocker;
			if (blocker == Entity.Null || blocker == vehicle)
			{
				continue;
			}
			float num2 = etaFrames * ((float)(i + 1) / (float)num);
			bool flag = false;
			float num3 = 0f;
			if (em.HasComponent<VehiclePublicTransport>(blocker))
			{
				uint departureFrame = em.GetComponentData<VehiclePublicTransport>(blocker).m_DepartureFrame;
				if (departureFrame != 0)
				{
					flag = true;
					num3 = (float)departureFrame - (float)now;
				}
			}
			if (flag && num2 >= num3)
			{
				continue;
			}
			// 占用者的离开时间未知（非公交车辆 / m_DepartureFrame=0）⇒ 无法推理，**不按住**：
			// 交给原版闭塞（我们只对“能算出它什么时候走”的占用者做提前量判断）。
			if (!flag)
			{
				continue;
			}
			m_UnknownBlockerHolds.Remove(vehicle);
			ModLog.Verbose("[P7] Hold: segment busy vehicle=" + vehicle.Index + " blocker=" + blocker.Index + " eta=" + num2.ToString("F0") + " occFreeIn=" + num3.ToString("F0") + " lane=" + lane.Index);
			return true;
		}
		return false;
	}

	private static uint ReleaseFrame(uint now)
	{
		return (now > 1800) ? (now - 1800) : 0u;
	}

	private void CleanFleetPollution(EntityManager em)
	{
		if (m_CleanedThisLoad)
		{
			return;
		}
		m_CleanedThisLoad = true;
		NativeArray<Entity> val = m_LineQuery.ToEntityArray(Allocator.Temp);
		int num = 0;
		try
		{
			int num2 = Math.Min(val.Length, 64);
			for (int i = 0; i < num2; i++)
			{
				Entity val2 = val[i];
				if (!em.Exists(val2) || !em.HasBuffer<RouteModifier>(val2))
				{
					continue;
				}
				DynamicBuffer<RouteModifier> buffer = em.GetBuffer<RouteModifier>(val2, false);
				int num3 = 1;
				if (buffer.Length > num3)
				{
					RouteModifier val3 = buffer[num3];
					float x = val3.m_Delta.x;
					float num4 = (em.HasComponent<TransportLine>(val2) ? em.GetComponentData<TransportLine>(val2).m_VehicleInterval : 0f);
					if (!(Math.Abs(x) < 0.0001f))
					{
						val3.m_Delta.x = 0f;
						buffer[num3] = val3;
						num++;
						ModLog.Info("[P7] cleaned line=" + val2.Index + " interval: old=" + num4.ToString("F2") + " new=" + num4.ToString("F2") + " (未改：本 Mod 只写 modifier) modifierDelta: old=" + x.ToString("F3") + " new=0");
					}
				}
			}
		}
		finally
		{
			val.Dispose();
		}
		ModLog.Info("[P7] fleet pollution cleanup done, cleaned=" + num);
	}

	/// <summary>
	/// 图定基准表条目结构体已归拢至 Runtime/LineBaseline.cs（tooltip 第 5 行与本测算共用）。
	/// </summary>
	private readonly Dictionary<Entity, LineBaseline> m_Baselines = new Dictionary<Entity, LineBaseline>(64);

	/// <summary>给 Tooltip 系统读的基准表快照（无数据返回 false——该线尚未被导出/统计过）。</summary>
	public bool TryGetLineBaseline(Entity line, out LineBaseline baseline)
	{
		return m_Baselines.TryGetValue(line, out baseline);
	}

	/// <summary>
	/// P8：基准时刻表导出（每线每会话一次，EnableTimetableExport 门控，只读汇总）。
	/// 汇总：段运行时间中位数（ring ≥3 样本优先，否则 RouteSegment 段中位数）、全线停站中位数、
	/// 循环时间估算（stops × (段中位 + 停站中位)）、各 RouteSegment 的 PathInformation 时长参考
	///（实测量级偏高 3–10×，仅段间相对形状参考——第十七条教训）。
	/// 分站级聚合（每站各自的 leg/dwell）待 dwell/leg 按站分桶后再细化。
	/// </summary>
	private void ExportTimetable(EntityManager em, RailCapacityGuardSetting s, Entity line, uint now, float fpm)
	{
		// 重计算每线每会话一次（填 m_Baselines 供 tooltip 第 5 行）；日志仍受 EnableTimetableExport 门控
		if (m_Baselines.ContainsKey(line) || !em.HasBuffer<Game.Routes.RouteWaypoint>(line))
		{
			return;
		}

		int stops = em.GetBuffer<Game.Routes.RouteWaypoint>(line, true).Length;
		if (stops <= 0)
		{
			return;
		}

		float legMedian = ((m_SegmentTime.GetLineSampleCount(line) >= 3) ? m_SegmentTime.GetLineMedian(line) : 0f);
		if (legMedian <= 0f)
		{
			m_SegmentTime.TryGetLineMedianLegFrames(em, line, m_Timebase.UnitMinutes, fpm, 0.5f * fpm, now, out legMedian);
		}

		List<string> segRef = new List<string>(stops);
		if (em.HasBuffer<RouteSegment>(line))
		{
			DynamicBuffer<RouteSegment> segments = em.GetBuffer<RouteSegment>(line, true);
			int n = Math.Min(segments.Length, stops);
			for (int i = 0; i < n; i++)
			{
				Entity segment = segments[i].m_Segment;
				if (segment == Entity.Null || !em.Exists(segment) || !em.HasComponent<PathInformation>(segment))
				{
					segRef.Add("?");
					continue;
				}

				float units = em.GetComponentData<PathInformation>(segment).m_Duration;
				segRef.Add((units > 0f) ? (UnitConversion.UnitsToFrames(units, m_Timebase.UnitMinutes, fpm) / fpm).ToString("F1") : "?");
			}
		}

		float dwellMedian = m_SegmentTime.GetMedianDwell(line);
		float roundTripMinutes = (legMedian + dwellMedian) * stops / fpm;
		// 国铁补丁⑦ 旅行速度 v旅 = 圈内里程 / 圈时（含停站）——运行图编制的核心质量指标。
		// 里程取各 RouteSegment PathInformation.m_Distance（世界单位）之和；时间换算成仿真秒
		// （frames/60），再经 SpeedToKmh（世界尺度 1.8）对齐游戏速度表口径。
		float totalDistance = 0f;
		if (em.HasBuffer<RouteSegment>(line))
		{
			DynamicBuffer<RouteSegment> segments = em.GetBuffer<RouteSegment>(line, true);
			int n = Math.Min(segments.Length, CnRailwayTuning.kMaxDistanceSegments);
			for (int i = 0; i < n; i++)
			{
				Entity segment = segments[i].m_Segment;
				if (segment == Entity.Null || !em.Exists(segment) || !em.HasComponent<PathInformation>(segment))
				{
					continue;
				}

				float distance = em.GetComponentData<PathInformation>(segment).m_Distance;
				if (distance > 0f)
				{
					totalDistance += distance;
				}
			}
		}
		float travelSpeedKmh = ((roundTripMinutes > 0.01f) ? UnitConversion.SpeedToKmh(totalDistance / (roundTripMinutes * fpm / 60f)) : 0f);
		// 国铁补丁⑤（算法测算，2026-09-27）：运行图三要素——
		//   需要车底数 = ⌈圈时 / 发车间隔⌉（N = T周/I）；通过能力 ≈ 60/I 对每小时；
		//   现有车数取 RouteVehicle buffer 实长。图定间隔用原版 m_VehicleInterval（units→分钟）。
		float intervalMinutes = 0f;
		if (em.HasComponent<TransportLine>(line))
		{
			intervalMinutes = UnitConversion.UnitsToMinutes(em.GetComponentData<TransportLine>(line).m_VehicleInterval, m_Timebase.UnitMinutes);
		}
		int vehiclesNow = 0;
		if (em.HasBuffer<RouteVehicle>(line))
		{
			vehiclesNow = em.GetBuffer<RouteVehicle>(line, true).Length;
		}
		float suggestedFleet = ((intervalMinutes > 0.01f) ? (float)Math.Ceiling(roundTripMinutes / intervalMinutes) : 0f);
		float capacityPerHour = ((intervalMinutes > 0.01f) ? (60f / intervalMinutes) : 0f);
		// 国铁测算补充（2026-09-27）：车辆不足时实际间隔 = 圈时/实有车数（原版车队由间隔推导，
		// vanilla_interactions §2，"车少"只出现在买车间隙/坏车），此时实际间隔主导图定节奏——
		// 通过能力按图定间隔算是理论上限，实际达不到。两个数并列输出，缺车一眼可见。
		float actualIntervalMinutes = ((vehiclesNow > 0) ? (roundTripMinutes / vehiclesNow) : 0f);
		float actualCapacityPerHour = ((actualIntervalMinutes > 0.01f) ? (60f / actualIntervalMinutes) : 0f);
		ModLog.Info("[P8] timetable line=" + line.Index + " stops=" + stops
			+ " legMedian=" + (legMedian / fpm).ToString("F1") + "min"
			+ " dwellMedian=" + (dwellMedian / fpm).ToString("F1") + "min"
			+ " roundTrip≈" + roundTripMinutes.ToString("F1") + "min"
			+ " legSamples=" + m_SegmentTime.GetLineSampleCount(line)
			+ " segRef(min)=[" + string.Join(",", segRef) + "]"
			+ " | fleet now=" + vehiclesNow + " need≈" + suggestedFleet.ToString("F0")
			+ " interval=" + intervalMinutes.ToString("F1") + "min"
			+ " capacity≈" + capacityPerHour.ToString("F1") + "/h"
			+ " actual≈" + actualIntervalMinutes.ToString("F1") + "min/" + actualCapacityPerHour.ToString("F1") + "per-h"
			+ " v旅=" + travelSpeedKmh.ToString("F1") + "km/h");

		m_Baselines[line] = new LineBaseline
		{
			Stops = stops,
			RoundTripMinutes = roundTripMinutes,
			VehiclesNow = vehiclesNow,
			NeedFleet = suggestedFleet,
			ActualIntervalMinutes = actualIntervalMinutes,
			TravelSpeedKmh = travelSpeedKmh,
			OnTimeCount = 0,
			LateCount = 0
		};
	}

	private void ClearPerLoadState()
	{
		m_States.Clear();
		m_LastDecision.Clear();
		m_TooltipInfo.Clear();
		m_SegmentTime.Clear();
		m_FirstEntryLogged.Clear();
		m_LastHeadwayLogged.Clear();
		m_TerminusLogged.Clear();
		m_SegmentUnknownLogged.Clear();
		m_UnknownBlockerHolds.Clear();
		m_SpeedProbeLogged.Clear();
		m_ZoneLastRelease.Clear();
		m_ZoneLastLine.Clear();
		m_Baselines.Clear();
	}

	/// <summary>
	/// 原版权威“正在本站登车”判定：站台实体上的 Game.Routes.BoardingVehicle.m_Vehicle == 本车。
	/// 依据：反编译 TransportBoardingHelpers —— BeginBoarding 设 BoardingVehicle、EndBoarding 清它并清车辆 Boarding 位
	///（归档 _analysis/vanilla_research/README.md §3）；TT 同用法 TimetableDispatchSystem.cs:956-963。
	/// 只遍历本线路的 RouteWaypoint（≤32），不遍历全城。
	/// </summary>
	/// <summary>
	/// 第二十一条：读本车路径位置读数（O(1)；不枚举前方车道、不累加 Curve.m_Length）。
	/// elementIndex = PathOwner.m_ElementIndex（原版随行进推进的"当前元素索引"，权威进度分子）；
	/// elementCount = PathElement buffer 长度（行驶中恒定，仅新寻路结果应用时变化 → 用作重寻路检测）；
	/// navLen = TrainNavigationLane.Length（原版维护的前方剩余车道数，0 = 到终点；兜底）。
	/// </summary>
	private void ReadPathPosition(EntityManager em, Entity vehicle, out int elementCount, out int elementIndex, out int navLen)
	{
		elementCount = 0;
		elementIndex = 0;
		navLen = 0;
		if (em.HasComponent<PathOwner>(vehicle))
		{
			elementIndex = em.GetComponentData<PathOwner>(vehicle).m_ElementIndex;
		}
		if (em.HasBuffer<PathElement>(vehicle))
		{
			elementCount = em.GetBuffer<PathElement>(vehicle, true).Length;
		}
		if (em.HasBuffer<TrainNavigationLane>(vehicle))
		{
			navLen = em.GetBuffer<TrainNavigationLane>(vehicle, true).Length;
		}
	}

	/// <summary>发车锚定：记录本段起始元素索引与元素总数（分母），Progress 归零。</summary>
	private void AnchorLegProgress(EntityManager em, Entity vehicle, ref VehicleSchedule schedule, uint now)
	{
		int elementCount;
		int elementIndex;
		int navLen;
		ReadPathPosition(em, vehicle, out elementCount, out elementIndex, out navLen);
		bool hasOwner = em.HasComponent<PathOwner>(vehicle);
		// 陈旧 buffer 守卫（2026-09-27 现象1 根因）：发车锚定若读到的还是上一段的旧路径
		//（idx 已接近末端 / cnt=0），p 会瞬间锁死高位 → legEta≈0 且"稳定地错"。
		// 此刻位置不可信 → 判无效，走时间减法；等本段新路径就绪（重寻路/下次锚定）再恢复。
		if (elementCount <= 0 || elementIndex >= elementCount - 2)
		{
			schedule.LegProgress = 0f;
			schedule.LegProgressValid = false;
			schedule.LegStartElementCount = elementCount;
			schedule.LegStartElementIndex = elementIndex;
			schedule.LegStartNavLen = navLen;
			schedule.LastElementCount = elementCount;
			schedule.LastElementIndex = elementIndex;
			schedule.LastNavLen = navLen;
			schedule.ProgressLoggedDecile = -1;
			return;
		}
		schedule.LegStartElementCount = elementCount;
		schedule.LegStartElementIndex = elementIndex;
		schedule.LegStartNavLen = navLen;
		schedule.LastElementCount = elementCount;
		schedule.LastElementIndex = elementIndex;
		schedule.LastNavLen = navLen;
		schedule.LegProgress = 0f;
		schedule.LegProgressValid = ((elementCount > 0 && hasOwner) || navLen > 1);
		schedule.ProgressLoggedDecile = -1;
	}

	/// <summary>
	/// 每 tick 更新本段 Progress（仅在车辆已离站/运行中调用）。
	/// 分子用 PathOwner.m_ElementIndex（原版随行进推进；"修订 A"的 buffer 长度差作废——行驶中长度恒定）。
	/// 边界1：buffer 长度变化或元素索引回退（新寻路结果被应用）→ 重新锚定，避免 progress 倒退或为负。
	/// 边界2：cnt 与 navLen 归零（到终点 / 外连离图）→ Progress=1、停止 ETA，交回原版。
	/// </summary>
	private void UpdateLegProgress(EntityManager em, Entity vehicle, ref VehicleSchedule schedule, uint now)
	{
		if (!schedule.LegProgressValid)
		{
			return;
		}

		int elementCount;
		int elementIndex;
		int navLen;
		ReadPathPosition(em, vehicle, out elementCount, out elementIndex, out navLen);

		// 边界1（重寻路）：新寻路结果被应用时 ProcessResultsJob 会 Clear/RemoveRange 并把 m_ElementIndex 归零
		//（PathfindJobs.decompiled.cs:1152-1166），表现为 buffer 长度变化或索引回退/低于锚点 → 重新锚定。
		bool repath = (schedule.LastElementCount > 0 && elementCount != schedule.LastElementCount)
			|| (schedule.LastElementIndex > 0 && elementIndex < schedule.LastElementIndex)
			|| (elementCount > 0 && elementIndex < schedule.LegStartElementIndex);
		if (repath)
		{
			AnchorLegProgress(em, vehicle, ref schedule, now);
			ModLog.Verbose("[P7] pos reanchor vehicle=" + vehicle.Index + " cnt=" + elementCount + " idx=" + elementIndex + " (repath)");
			return;
		}

		// 边界2（到终点/离图）：AND 判定（cnt<=0 && navLen<=0）→ Progress=1、停止 ETA、交回原版、不硬算。
		if (elementCount <= 0 && navLen <= 0)
		{
			schedule.LegProgress = 1f;
			schedule.LegProgressValid = false;
			return;
		}

		schedule.LastElementCount = elementCount;
		schedule.LastElementIndex = elementIndex;
		schedule.LastNavLen = navLen;
		float progress = -1f;
		if (schedule.LegStartElementCount > 0 && elementCount > 0)
		{
			// 手册第二十一条原公式：progress = (currentIndex − startElementIndex) / 本段元素跨度。
			// 末元素索引 = count−1，到站时 elementIndex = count−1 ⇒ progress 恰为 1。
			int span = schedule.LegStartElementCount - 1 - schedule.LegStartElementIndex;
			if (span < 1)
			{
				span = 1;
			}
			progress = (float)(elementIndex - schedule.LegStartElementIndex) / (float)span;
		}
		else if (schedule.LegStartNavLen > 1 && navLen > 0)
		{
			// 兜底（修订 B）：TrainNavigationLane.Length = 前方剩余车道数，仅 PathElement 不可用时。
			progress = (float)(schedule.LegStartNavLen - navLen) / (float)(schedule.LegStartNavLen - 1);
		}
		if (progress < 0f)
		{
			return;
		}
		if (progress > 1f)
		{
			progress = 1f;
		}
		if (progress > schedule.LegProgress)
		{
			schedule.LegProgress = progress;   // 单调不减
		}

		int decile = (int)(schedule.LegProgress * 10f);
		if (decile != schedule.ProgressLoggedDecile)
		{
			schedule.ProgressLoggedDecile = decile;
			ModLog.Verbose("[P7] pos vehicle=" + vehicle.Index + " startIdx=" + schedule.LegStartElementIndex + " idx=" + elementIndex + " cnt=" + elementCount + " navLen=" + navLen + " p=" + (schedule.LegProgress * 100f).ToString("F0") + "%");
		}
	}
	private bool IsBoardingAtLineStop(EntityManager em, Entity line, Entity vehicle)
	{
		if (line == Entity.Null || !em.Exists(line) || !em.HasBuffer<Game.Routes.RouteWaypoint>(line))
		{
			return false;
		}

		DynamicBuffer<Game.Routes.RouteWaypoint> waypoints = em.GetBuffer<Game.Routes.RouteWaypoint>(line, true);
		int count = waypoints.Length > 32 ? 32 : waypoints.Length;
		for (int i = 0; i < count; i++)
		{
			Entity stop = waypoints[i].m_Waypoint;
			if (stop == Entity.Null || !em.Exists(stop) || !em.HasComponent<Game.Routes.BoardingVehicle>(stop))
			{
				continue;
			}

			if (em.GetComponentData<Game.Routes.BoardingVehicle>(stop).m_Vehicle == vehicle)
			{
				return true;
			}
		}

		return false;
	}

	private bool IsManagedVehicle(EntityManager em, Entity vehicle)
	{
		return vehicle != Entity.Null && em.Exists(vehicle) && em.HasComponent<VehiclePublicTransport>(vehicle) && em.HasComponent<TrainCurrentLane>(vehicle);
	}

	private void RunFleetPass(EntityManager em, RailCapacityGuardSetting s, float fpm)
	{
		NativeArray<Entity> val = m_LineQuery.ToEntityArray(Allocator.Temp);
		try
		{
			int num = Math.Min(val.Length, 64);
			for (int i = 0; i < num; i++)
			{
				Entity val2 = val[i];
				if (!m_States.TryGetValue(val2, out var value) || !em.HasBuffer<RouteVehicle>(val2) || !em.HasComponent<TransportLine>(val2))
				{
					continue;
				}
				TransportLine componentData = em.GetComponentData<TransportLine>(val2);
				float intervalMinutes = ((componentData.m_VehicleInterval > 0.01f) ? componentData.m_VehicleInterval : 5f);
				float loopMinutes = m_Fleet.GetLoopMinutes(em, val2, fpm);
				if (loopMinutes <= 0.01f)
				{
					continue;
				}
				int num2 = FleetPolicyService.ComputeVanillaTargetCount(loopMinutes, intervalMinutes);
				int length = em.GetBuffer<RouteVehicle>(val2, true).Length;
				bool flag = value.EarlyCount >= 2 && length < num2;
				bool flag2 = value.HoldCount >= 2 && length > num2;
				value.EarlyCount = 0;
				value.HoldCount = 0;
				if (flag)
				{
					value.FleetUpStreak++;
					value.FleetDownStreak = 0;
				}
				else if (flag2)
				{
					value.FleetDownStreak++;
					value.FleetUpStreak = 0;
				}
				else
				{
					value.FleetUpStreak = 0;
					value.FleetDownStreak = 0;
				}
				bool flag3 = value.FleetLastChangeFrame == 0 || m_Timebase.CurrentFrame - value.FleetLastChangeFrame >= 512;
				int num3 = length;
				if ((value.FleetUpStreak >= 2) & flag3)
				{
					num3 = length + 1;
				}
				else if ((value.FleetDownStreak >= 2) & flag3)
				{
					num3 = length - 1;
				}
				if (num3 < 1)
				{
					num3 = 1;
				}
				if (num3 != length)
				{
					float defaultIntervalMinutes = 5f;
					if (em.HasComponent<PrefabRef>(val2))
					{
						Entity prefab = em.GetComponentData<PrefabRef>(val2).m_Prefab;
						if (prefab != Entity.Null && em.Exists(prefab) && em.HasComponent<TransportLineData>(prefab))
						{
							defaultIntervalMinutes = em.GetComponentData<TransportLineData>(prefab).m_DefaultVehicleInterval;
						}
					}
					if (m_Fleet.TrySetFleet(em, val2, num3, defaultIntervalMinutes, loopMinutes, out var _, out var reason))
					{
						value.FleetLastChangeFrame = m_Timebase.CurrentFrame;
						value.FleetUpStreak = 0;
						value.FleetDownStreak = 0;
					}
					else
					{
						ModLog.Verbose("[Fleet] skip line " + val2.Index + ": " + reason);
					}
				}
				m_States[val2] = value;
			}
		}
		finally
		{
			val.Dispose();
		}
	}

	protected override void OnDestroy()
	{
		if (m_Fleet != null)
		{
			foreach (KeyValuePair<Entity, LineRuntimeState> state in m_States)
			{
				try
				{
					m_Fleet.ClearFleet(((ComponentSystemBase)this).EntityManager, state.Key);
				}
				catch (Exception)
				{
				}
			}
		}
		try
		{
			m_PathfindCost.Restore(EntityManager);   // P4：卸载前恢复原版寻路数据
		}
		catch (Exception)
		{
		}
		ClearPerLoadState();
		base.OnDestroy();
	}
}
}
