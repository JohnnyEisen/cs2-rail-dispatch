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

	private uint m_LastZoneRebuild;

	private readonly Dictionary<Entity, byte> m_LastDecision = new Dictionary<Entity, byte>(256);

	private readonly SegmentTimeService m_SegmentTime = new SegmentTimeService();

	private readonly HashSet<Entity> m_FirstEntryLogged = new HashSet<Entity>();

	private readonly Dictionary<Entity, float> m_LastHeadwayLogged = new Dictionary<Entity, float>(64);

	private readonly HashSet<Entity> m_TerminusLogged = new HashSet<Entity>();

	private readonly HashSet<Entity> m_SegmentUnknownLogged = new HashSet<Entity>();

	private readonly Dictionary<Entity, VehicleSchedule> m_VehicleSchedule = new Dictionary<Entity, VehicleSchedule>(256);

	private readonly Dictionary<Entity, VehicleTooltipInfo> m_TooltipInfo = new Dictionary<Entity, VehicleTooltipInfo>(256);

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
			ModLog.Verbose("[P7] window: writes=" + m_WindowWrites + " noData=" + m_WindowNoData + " holds=" + m_WindowHolds + " departs=" + m_WindowDeparts);
			m_WindowWrites = 0;
			m_WindowNoData = 0;
			m_WindowHolds = 0;
			m_WindowDeparts = 0;
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
		if (!settings.EnableTimetableDispatch)
		{
			return;
		}
		NativeArray<Entity> val = m_LineQuery.ToEntityArray(Allocator.Temp);
		try
		{
			int num3 = (LastLineCount = Math.Min(val.Length, 64));
			LastWriteCount = 0;
			LastNoDataCount = 0;
			for (int i = 0; i < num3; i++)
			{
				ProcessLine(entityManager, settings, val[i], currentFrame, framesPerMinute, unitMinutes);
			}
		}
		finally
		{
			val.Dispose();
		}
	}

	private void ProcessLine(EntityManager em, RailCapacityGuardSetting s, Entity line, uint now, float fpm, float unitMinutes)
	{
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
		float num = ((componentData.m_VehicleInterval > 0.01f) ? componentData.m_VehicleInterval : 5f);
		float num2 = UnitConversion.UnitsToMinutes(num, unitMinutes);
		// 站间运行时间：主源 = 每辆车自己的 PathInformation.m_Duration（发车时读一次，见下）。
		// 这里只取线路中位数，作为回退 ① / 诊断值。
		float value3 = m_SegmentTime.GetLineMedian(line);
		int value2 = (value3 > 0f) ? 1 : 0;
		value.SegmentRunFrames = value3;
		value.MinHeadwayFrames = Math.Max(8f, s.MinHeadwayMinutes * fpm);
		if (value3 <= 0f && m_SegmentUnknownLogged.Add(line))
		{
			ModLog.Info("[P7] line=" + line.Index + " segment run unknown: leg=none(该车本 tick 无上一段样本) lineEma=" + value2 + " samples(<" + 2 + " 或 <" + (0.5f * fpm).ToString("F0") + "f) fallback=m_Duration(需 ≥" + 0.5f + " unit) -> 本站图定待定，交回原版");
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
			VehiclePublicTransport componentData2 = em.GetComponentData<VehiclePublicTransport>(vehicle);
			if (m_Resolver.TryGetCurrentStationLane(em, vehicle, out var _) && (componentData2.m_State & PublicTransportFlags.AbandonRoute) != 0 && (componentData2.m_State & PublicTransportFlags.Boarding) == 0)
			{
				ref PublicTransportFlags state = ref componentData2.m_State;
				state = (PublicTransportFlags)((uint)state & 0xFFFFFDFFu);
				em.SetComponentData<VehiclePublicTransport>(vehicle, componentData2);
				ModLog.Verbose("[P7] stripped AbandonRoute (at platform) vehicle=" + vehicle.Index);
			}
			float num5 = (em.HasComponent<TrainNavigation>(vehicle) ? em.GetComponentData<TrainNavigation>(vehicle).m_Speed : (-1f));
			bool flag2 = m_Resolver.TryGetCurrentStationLane(em, vehicle, out var _) || (componentData2.m_State & PublicTransportFlags.Boarding) != 0;
			if (!m_VehicleSchedule.TryGetValue(vehicle, out var value5))
			{
				value5 = default(VehicleSchedule);
			}
			uint num9;
			if (flag2 || (value5.AtStop && num5 < 0.5f))   // 闩锁：停住时保持；一旦开动就解除（避免被原版放走后仍显示待发）
			{
				if (!value5.AtStop)
				{
					float num6 = 0.5f * fpm;

					// 自检：上一段的 估计 vs 实际（实际 = 本站进站帧 − 上一站发车帧），比值越界即判不可信
					if (value5.LegEstimateFrames > 0f && value5.LegStartFrame != 0u && now > value5.LegStartFrame)
					{
						float numActual = (float)(now - value5.LegStartFrame);
						float numRatio = (numActual > 0f) ? (value5.LegEstimateFrames / numActual) : 0f;
						if (numRatio < 0.5f || numRatio > 2.0f)
						{
							ModLog.Warn("[P7] leg sanity vehicle=" + vehicle.Index + " estimate=" + value5.LegEstimateFrames.ToString("F0") + " actual=" + numActual.ToString("F0") + " ratio=" + numRatio.ToString("F2"));
							value5.LegEstimateOk = false;
						}

						m_SegmentTime.RecordLeg(line, numActual);
					}

					// 本段完成 → 允许下次发车重新读一次估计
					value5.LegStartFrame = 0u;

					float num7 = ((value5.LegEstimateOk && value5.LegEstimateFrames >= num6) ? value5.LegEstimateFrames : value3);
					if (num7 < num6)
					{
						// 站点现场读一次（实测：停站期间 m_Duration 稳定，等于本段运行时间）
						float numHere;
						if (m_SegmentTime.TryReadVehicleLegFrames(em, vehicle, unitMinutes, fpm, num6, out numHere))
						{
							num7 = numHere;
							value5.LegEstimateFrames = numHere;
							value5.LegEstimateOk = true;
						}
						else
						{
							float numMean;
							if (m_SegmentTime.TryGetLineMeanLegFrames(em, line, unitMinutes, fpm, num6, now, out numMean))
							{
								num7 = numMean;
							}
						}
					}

					uint num8 = ((value5.LastDepartFrame != 0) ? value5.LastDepartFrame : now);
					value5.ScheduleUnknown = num7 < num6;
					value5.PlannedDepartFrame = (value5.ScheduleUnknown ? now : (num8 + (uint)(num7 + value5.LastDwellFrames)));
					value5.AtStop = true;
					value5.StopEnterFrame = now;
					ModLog.Verbose("[P7] schedule locked vehicle=" + vehicle.Index + " base=" + num8 + " leg=" + num7.ToString("F0") + " dwell=" + value5.LastDwellFrames.ToString("F0") + " planned=" + value5.PlannedDepartFrame);
				}
				num9 = value5.PlannedDepartFrame;
			}
			else
			{
				value5.AtStop = false;
				num9 = now;
			}
			// 本段剩余帧（空间层 ETA 用）；未知 = -1
			float legEta = -1f;
			if (value5.LegEstimateOk && value5.LegEstimateFrames >= 0.5f * fpm)
			{
				float legElapsed = (value5.LegStartFrame != 0u && now > value5.LegStartFrame) ? (float)(now - value5.LegStartFrame) : 0f;
				legEta = value5.LegEstimateFrames - legElapsed;
				if (legEta < 0f)
				{
					legEta = 0f;
				}
			}

			// tooltip 第 2 行数据源：本车本段估计（没读到则用线路中位数）
			value.SegmentRunFrames = (value5.LegEstimateFrames > 0f) ? value5.LegEstimateFrames : value3;

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
					m_VehicleSchedule[vehicle] = value5;
					RecordTooltipInfo(line, vehicle, value, num9, target, now, DepartureDecision.NoData, VehicleStateKind.Boarding, etaOut, num5, "[0] boarding in progress -> 不干预 (" + reason + ")");
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
			// hold 写放大修复：只有 Depart 且未放行（变化时写）/ Hold 且已存帧已到期（需要延长）才写
			bool needWrite = (departureDecision == DepartureDecision.Depart)
				? (componentData2.m_DepartureFrame > now && componentData2.m_DepartureFrame != target)
				: (componentData2.m_DepartureFrame <= now && componentData2.m_DepartureFrame != target);
			if (needWrite)
			{
				componentData2.m_DepartureFrame = target;
				em.SetComponentData<VehiclePublicTransport>(vehicle, componentData2);
				LastWriteCount++;
				m_WindowWrites++;
				if (departureDecision == DepartureDecision.Depart)
				{
					// 每段读一次（实测确认：此刻 m_Duration 稳定且量级正确）
					value5.LegStartFrame = now;   // 真实发车时刻（target 是 now-1800 的放行值，不能当起点）
					float legFrames;
					if (m_SegmentTime.TryReadVehicleLegFrames(em, vehicle, unitMinutes, fpm, 0.5f * fpm, out legFrames))
					{
						value5.LegEstimateFrames = legFrames;
						value5.LegEstimateOk = true;
					}
					else
					{
						value5.LegEstimateFrames = 0f;
						value5.LegEstimateOk = false;
					}
				}
			}
			if (departureDecision == DepartureDecision.Depart && value5.LegStartFrame == 0u)
			{
				// 每段一次：本段估计（与是否写入组件无关，避免 alreadyReleased 时漏读）
				value5.LegStartFrame = now;
				float legFrames2;
				if (m_SegmentTime.TryReadVehicleLegFrames(em, vehicle, unitMinutes, fpm, 0.5f * fpm, out legFrames2))
				{
					value5.LegEstimateFrames = legFrames2;
					value5.LegEstimateOk = true;
				}
			}

			if (departureDecision == DepartureDecision.Depart)
			{
				value.LastWrittenDepartureFrame = target;

				// 记录本站真实发车帧 + 实测停站 → 下一站图定的基准（此前从未写入，base 永远是 now）
				value5.LastDepartFrame = target;
				value5.AtStop = false;   // 已发车 → 清除站点闩锁，下次到站重新锁定（图定不再滑动）
				if (value5.StopEnterFrame != 0u && now > value5.StopEnterFrame)
				{
					value5.LastDwellFrames = (float)(now - value5.StopEnterFrame);
				}

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
		etaOut = -1f;
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
				target = ReleaseFrame(now);
				bypassSlotFloor = true;
				kind = VehicleStateKind.Releasing;
				reason = "[2] at stop, no upcoming platform -> depart";
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
		// 空间层 ETA = 本段剩余帧（来自每段一次读到的 m_Duration）；未知 = -1 → 不碰
		float num2 = (etaOut = legEtaFrames);
		string source = "leg";   // 旧 ETA 来源字段（现固定为 leg）
		if (num2 < 0f)
		{
			kind = VehicleStateKind.DataUnavailable;
			reason = "[2] leg ETA unavailable (no m_Duration / sanity failed) -> NoData";
			return DepartureDecision.NoData;
		}
		CapacityVerdict capacityVerdict = m_Capacity.QueryArrival(em, lane2, vehicle, now, num2, s.SafetyMarginFrames);
		if (capacityVerdict.Confidence == Confidence.Unavailable)
		{
			kind = VehicleStateKind.DataUnavailable;
			reason = "[2] verdict unavailable (" + capacityVerdict.Reason.ToString() + ") platform=" + lane2.Index + " eta=" + num2.ToString("F0") + " source=" + source + " -> NoData";
			return DepartureDecision.NoData;
		}
		if (IsUpcomingSegmentBusy(em, vehicle, num2, now))
		{
			kind = VehicleStateKind.SegmentBusy;
			reason = "[1x] segment busy -> Hold eta=" + num2.ToString("F0");
			return DepartureDecision.Hold;
		}
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
				target = ReleaseFrame(now);
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
		if (s.EnableThroatCoordination && m_Throat.TryGetZoneId(lane2, out var zoneId) && m_Throat.IsBusy(em, zoneId, vehicle))
		{
			kind = VehicleStateKind.WaitingThroat;
			reason = "[3] throat zone " + zoneId + " busy -> Hold";
			return DepartureDecision.Hold;
		}
		if (s.EnableEarlyDeparture && state.MaxEarlyFrames >= 1f)
		{
			uint num6 = (uint)Math.Max(now, (float)planned - state.MaxEarlyFrames);
			if (now >= num6 && HasRearPressure(em, vehicles, vehicleCount, vehicle, lane2))
			{
				target = ReleaseFrame(now);
				early = true;
				bypassSlotFloor = true;
				kind = VehicleStateKind.Releasing;
				reason = "[4] rear pressure -> early depart target=now-1800 (planned=" + planned + ")";
				return DepartureDecision.Depart;
			}
		}
		target = ReleaseFrame(now);
		bypassSlotFloor = true;
		kind = VehicleStateKind.Releasing;
		reason = "[5] depart target=now-1800 (planned=" + planned + ")";
		return DepartureDecision.Depart;
	}

	public bool TryGetVehicleInfo(Entity vehicle, out VehicleTooltipInfo info)
	{
		return m_TooltipInfo.TryGetValue(vehicle, out info);
	}

	private void RecordTooltipInfo(Entity line, Entity vehicle, LineRuntimeState state, uint planned, uint target, uint now, DepartureDecision decision, VehicleStateKind kind, float etaFrames, float speed, string reason)
	{
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
			m_UnknownBlockerHolds.TryGetValue(vehicle, out var value);
			if (!flag && value >= 8)
			{
				m_UnknownBlockerHolds.Remove(vehicle);
				ModLog.Verbose("[P7] segment busy (unknown blocker) bounded release vehicle=" + vehicle.Index + " blocker=" + blocker.Index + " holds=" + value + " lane=" + lane.Index);
				continue;
			}
			if (!flag)
			{
				m_UnknownBlockerHolds[vehicle] = value + 1;
			}
			else
			{
				m_UnknownBlockerHolds.Remove(vehicle);
			}
			ModLog.Verbose("[P7] Hold: segment busy vehicle=" + vehicle.Index + " blocker=" + blocker.Index + " eta=" + num2.ToString("F0") + " occFreeIn=" + (flag ? num3.ToString("F0") : "unknown") + " holds=" + (value + 1) + " lane=" + lane.Index);
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
				if (!m_States.TryGetValue(val2, out var value) || !em.HasBuffer<RouteVehicle>(val2))
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
		ClearPerLoadState();
		base.OnDestroy();
	}
}
}
