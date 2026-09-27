using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using RailCapacityGuard.Utils;

// 消歧：Game.Net.TrackLane 与 Game.Prefabs.TrackLane（ComponentBase 类）同名
using NetTrackLane = Game.Net.TrackLane;
// 消歧：Game.Prefabs.PublicTransport（prefab 类）与 Game.Vehicles.PublicTransport（车辆组件）
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace RailCapacityGuard.Utils
{
    /// <summary>
    /// 运行时实验采集器（只读优先；写入型部分必须显式开启）。
    ///
    /// 开关（三层，全部可关闭）：
    ///   1. 游戏内总闸：Settings.EnableDiagnosticLogging（关 = 采集器完全不工作）
    ///   2. 启动标志文件：&lt;采集目录&gt;\exp_start.flag（存在才启动）
    ///   3. 写入许可文件：&lt;采集目录&gt;\exp_write.flag（存在才允许写 Blocker.m_MaxSpeed）
    ///
    /// 采集目录（OutputDir）：默认 %USERPROFILE%\RailCapacityGuard\_analysis，
    ///   可用环境变量 RAILGUARD_SAMPLER_DIR 覆盖（本机路径不写进源码）。
    /// 输出（每条记录：frame,experiment,entity,field,value）：
    ///   &lt;采集目录&gt;\expC_time.csv
    ///   &lt;采集目录&gt;\expA_cost.csv
    ///   &lt;采集目录&gt;\expB_blocker.csv
    /// 完成后写 exp_done.flag 并自禁用（不再采样）。
    ///
    /// 只读字段全部经反射表确认；写入型字段仅 Blocker.m_MaxSpeed（dump:110293），
    /// 原值保存 + 窗口结束/异常时回写。
    /// </summary>
    public static class ExperimentSampler
    {
        /// <summary>实验采集目录：默认 %USERPROFILE%\RailCapacityGuard\_analysis，可用环境变量 RAILGUARD_SAMPLER_DIR 覆盖。</summary>
        private static readonly string OutputDir = ResolveOutputDir();

        private static string ResolveOutputDir()
        {
            string overridden = Environment.GetEnvironmentVariable("RAILGUARD_SAMPLER_DIR");
            if (!string.IsNullOrEmpty(overridden))
            {
                return overridden;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RailCapacityGuard", "_analysis");
        }
        private const string StartFlagName = "exp_start.flag";
        private const string WriteFlagName = "exp_write.flag";
        private const string DoneFlagName = "exp_done.flag";

        private const string FileV = "expV_binding.csv";
        private const string FileD = "expD_binding.csv";
        private const string FileC = "expC_time.csv";
        private const string FileA = "expA_cost.csv";
        private const string FileB = "expB_blocker.csv";

        // 帧预算（采样频率见各阶段注释）
        // 阶段 D：玩家指定站台绑定链（Q1–Q6）
        // 阶段 V：V1（站台轨道 LaneReservation）+ V3（单车容量）
        private const int FramesV = 1200;
        private const int MaxStationLanes = 60;
        private const int MaxV3Trains = 20;

        private const int FramesD = 1200;            // ≈35-40 秒，覆盖 进站前→停稳→出站
        private const int IntervalDSnapshot = 150;   // PathTargets 周期快照（Q6：玩家中途改站台也能抓到）
        private const int MaxBindingTrains = 3;
        private const int MaxNavLanesShown = 12;
        private const int MaxPathTargetsHosts = 40;
        private const int MaxRoutes = 6;
        private const int MaxWaypointsPerRoute = 8;
        private const int NavLaneDumpInterval = 10;

        private const int FramesC = 600;
        private const int FramesABaseline = 300;
        private const int FramesAPerturb = 300;
        private const int FramesBRead = 200;
        private const int FramesBWrite = 300;
        private const int IntervalA = 60;
        private const int IntervalBWrite = 10;
        private const int MaxVehicles = 20;

        private enum Phase
        {
            NotStarted,
            V,
            D,
            C,
            ABase,
            APerturb,
            BRead,
            BWrite,
            Finished
        }

        private static Phase s_Phase = Phase.NotStarted;
        private static int s_PhaseFrame;
        private static bool s_WriteAllowed;
        private static string s_StartFlag;
        private static string s_WriteFlag;
        private static string s_DoneFlag;
        private static string s_FlagProblem;

        private static StreamWriter s_WriterV;
        private static StreamWriter s_WriterD;
        private static StreamWriter s_WriterC;
        private static StreamWriter s_WriterA;
        private static StreamWriter s_WriterB;

        private static Entity s_TargetVehicle = Entity.Null;
        private static Entity s_TargetLane = Entity.Null;
        private static bool s_HasOriginalBlocker;
        private static Blocker s_OriginalBlocker;
        private static int s_WriteCount;
        private static bool s_Faulted;

        private static readonly Dictionary<string, int> s_FieldErrors = new Dictionary<string, int>();
        private static readonly Dictionary<Entity, string> s_EntityBitmapCache = new Dictionary<Entity, string>();
        private static int s_BindingSnapshot;
        private static bool s_BindingInstallDumped;

        public static bool IsActive
        {
            get { return s_Phase != Phase.NotStarted && s_Phase != Phase.Finished && !s_Faulted; }
        }

        /// <summary>由 ExperimentSamplerSystem 每帧调用（GameSimulation 相位）。</summary>
        public static void Tick(EntityManager entityManager, World world, bool masterSwitchOn)
        {
            if (!masterSwitchOn || s_Faulted)
            {
                return;
            }

            try
            {
                if (s_Phase == Phase.NotStarted)
                {
                    if (!Begin())
                    {
                        return;
                    }
                }

                if (s_Phase == Phase.Finished)
                {
                    return;
                }

                switch (s_Phase)
                {
                    case Phase.V:
                        SampleV(entityManager, world);
                        break;
                    case Phase.D:
                        SampleD(entityManager, world);
                        break;
                    case Phase.C:
                        SampleC(entityManager, world);
                        break;
                    case Phase.ABase:
                    case Phase.APerturb:
                        SampleA(entityManager, world);
                        break;
                    case Phase.BRead:
                    case Phase.BWrite:
                        SampleB(entityManager, world);
                        break;
                }
            }
            catch (Exception ex)
            {
                s_Faulted = true;
                ModLog.Error("[EXP] sampler faulted, stopping: " + ex);
                RestoreBlocker(entityManager);
                Finish("faulted");
            }
        }

        // ---------------- 启动 ----------------

        private static bool Begin()
        {
            s_StartFlag = Path.Combine(OutputDir, StartFlagName);
            s_WriteFlag = Path.Combine(OutputDir, WriteFlagName);
            s_DoneFlag = Path.Combine(OutputDir, DoneFlagName);

            if (!Directory.Exists(OutputDir))
            {
                s_FlagProblem = "输出目录不存在: " + OutputDir;
                ModLog.Warn("[EXP] " + s_FlagProblem);
                s_Phase = Phase.Finished;
                return false;
            }

            if (!File.Exists(s_StartFlag))
            {
                // 未要求采集：保持静默，不刷日志
                s_FlagProblem = "未找到启动标志 " + s_StartFlag;
                s_Phase = Phase.Finished;
                return false;
            }

            if (File.Exists(s_DoneFlag))
            {
                ModLog.Info("[EXP] 已存在完成标志 " + s_DoneFlag + "，本次不再采集（删除该文件可重跑）");
                s_Phase = Phase.Finished;
                return false;
            }

            s_WriteAllowed = File.Exists(s_WriteFlag);
            ModLog.Info("[EXP] 采集开始 writeAllowed=" + s_WriteAllowed + " output=" + OutputDir);
            if (!s_WriteAllowed)
            {
                ModLog.Info("[EXP] 只读模式（缺少 " + s_WriteFlag + "）：实验 A 不做扰动、实验 B 不写 m_MaxSpeed");
            }

            OpenWriters();
            WriteConstantLines();
            s_Phase = Phase.V;
            s_PhaseFrame = 0;
            ModLog.Info("[EXP] phase=V frames=" + FramesV + " (V1 站台 LaneReservation / V3 单车容量)");
            return true;
        }

        private static void OpenWriters()
        {
            s_WriterV = Open(FileV);
            s_WriterD = Open(FileD);
            s_WriterC = Open(FileC);
            s_WriterA = Open(FileA);
            s_WriterB = Open(FileB);
        }

        private static StreamWriter Open(string fileName)
        {
            string path = Path.Combine(OutputDir, fileName);
            StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine("frame,experiment,entity,field,value");
            writer.Flush();
            ModLog.Info("[EXP] writing " + path);
            return writer;
        }

        private static void WriteConstantLines()
        {
            // 常量（编译期 const，dump 中带字面量值）
            WriteRow(s_WriterC, 0, "C", "const", "TimeSystem.kTicksPerDay", TimeSystem.kTicksPerDay.ToString(CultureInfo.InvariantCulture));
            WriteRow(s_WriterC, 0, "C", "const", "SimulationSystem.PENDING_FRAMES_SPEED_FACTOR", SimulationSystem.PENDING_FRAMES_SPEED_FACTOR.ToString("F9", CultureInfo.InvariantCulture));
        }

        // ---------------- 实验 C：帧 ↔ tick ↔ 秒 ----------------

        private static void SampleC(EntityManager entityManager, World world)
        {
            SimulationSystem simulation = world.GetExistingSystemManaged<SimulationSystem>();
            TimeSystem time = world.GetExistingSystemManaged<TimeSystem>();

            int frame = simulation != null ? (int)simulation.frameIndex : -1;

            WriteField(s_WriterC, frame, "C", "none", "simulation.frameIndex", frame.ToString(CultureInfo.InvariantCulture));
            WriteField(s_WriterC, frame, "C", "none", "wallClock.realtimeSinceStartup", F3(UnityEngine.Time.realtimeSinceStartup));
            if (simulation != null)
            {
                WriteField(s_WriterC, frame, "C", "none", "simulation.frameTime", F(simulation.frameTime));
                WriteField(s_WriterC, frame, "C", "none", "simulation.frameDuration", F(simulation.frameDuration));
            }

            if (time != null)
            {
                // 高精度：F4 不足以算帧↔日比例（上一轮 0.0023 只有两位有效数字）
                WriteField(s_WriterC, frame, "C", "none", "time.normalizedTime", F9(time.normalizedTime));
                WriteField(s_WriterC, frame, "C", "none", "time.normalizedDate", F9(time.normalizedDate));
                WriteField(s_WriterC, frame, "C", "none", "time.year", time.year.ToString(CultureInfo.InvariantCulture));
                WriteField(s_WriterC, frame, "C", "none", "time.daysPerYear", time.daysPerYear.ToString(CultureInfo.InvariantCulture));
            }

            // TimeSystem.GetTicks(TimeSettingsData, TimeData) —— 需要两个组件单例
            TrySampleTicks(entityManager, time, frame);

            // 车辆发车帧（VehicleTiming.m_LastDepartureFrame，dump:50385）
            TrySampleFirstDepartureFrame(entityManager, frame);

            Advance(FramesC);
        }

        private static void TrySampleTicks(EntityManager entityManager, TimeSystem time, int frame)
        {
            if (time == null)
            {
                return;
            }

            Entity settingsEntity = Entity.Null;
            Entity dataEntity = Entity.Null;
            try
            {
                EntityQuery settingsQuery = entityManager.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<TimeSettingsData>() });
                EntityQuery dataQuery = entityManager.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<TimeData>() });
                if (settingsQuery.CalculateEntityCount() == 1)
                {
                    settingsEntity = settingsQuery.GetSingletonEntity();
                }

                if (dataQuery.CalculateEntityCount() == 1)
                {
                    dataEntity = dataQuery.GetSingletonEntity();
                }
            }
            catch (Exception ex)
            {
                CountError("ticks.query", ex);
                return;
            }

            if (settingsEntity == Entity.Null || dataEntity == Entity.Null)
            {
                CountError("ticks.missingSingleton", null);
                return;
            }

            try
            {
                // 注意：TimeSystem.GetTicks(...) 在 1.6.2f1 里**非 public**（编译期 CS0122 证实），
                // 因此无法直接做帧→tick 换算；只采公开可见的 TimeData 字段与系统公开属性。
                TimeSettingsData settings = entityManager.GetComponentData<TimeSettingsData>(settingsEntity);
                TimeData data = entityManager.GetComponentData<TimeData>(dataEntity);
                WriteField(s_WriterC, frame, "C", "none", "timeSettings.present", settingsEntity.Index.ToString(CultureInfo.InvariantCulture));
                WriteField(s_WriterC, frame, "C", "none", "timeData.m_FirstFrame", data.m_FirstFrame.ToString(CultureInfo.InvariantCulture));
                WriteField(s_WriterC, frame, "C", "none", "timeData.TimeOffset", F(data.TimeOffset));
            }
            catch (Exception ex)
            {
                CountError("ticks.call", ex);
            }
        }

        private static void TrySampleFirstDepartureFrame(EntityManager entityManager, int frame)
        {
            try
            {
                EntityQuery query = entityManager.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<VehicleTiming>() });
                if (query.CalculateEntityCount() <= 0)
                {
                    CountError("vehicleTiming.empty", null);
                    return;
                }

                NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
                try
                {
                    VehicleTiming timing = entityManager.GetComponentData<VehicleTiming>(entities[0]);
                    WriteField(s_WriterC, frame, "C", Idx(entities[0]), "vehicleTiming.m_LastDepartureFrame", timing.m_LastDepartureFrame.ToString(CultureInfo.InvariantCulture));
                    WriteField(s_WriterC, frame, "C", Idx(entities[0]), "vehicleTiming.m_AverageTravelTime", F(timing.m_AverageTravelTime));
                }
                finally
                {
                    entities.Dispose();
                }
            }
            catch (Exception ex)
            {
                CountError("vehicleTiming", ex);
            }
        }

        // ---------------- 实验 A：代价变更后是否自动重算 ----------------

        private static void SampleA(EntityManager entityManager, World world)
        {
            SimulationSystem simulation = world.GetExistingSystemManaged<SimulationSystem>();
            int frame = simulation != null ? (int)simulation.frameIndex : -1;

            if (s_PhaseFrame % IntervalA == 0)
            {
                if (s_TargetVehicle == Entity.Null || !entityManager.Exists(s_TargetVehicle) || !entityManager.HasComponent<PathInformation>(s_TargetVehicle))
                {
                    s_TargetVehicle = FindVehicle(entityManager, new ComponentType[] { ComponentType.ReadOnly<PathInformation>(), ComponentType.ReadOnly<PathOwner>() });
                }

                if (s_TargetVehicle != Entity.Null && entityManager.Exists(s_TargetVehicle))
                {
                    string id = Idx(s_TargetVehicle);
                    try
                    {
                        PathInformation info = entityManager.GetComponentData<PathInformation>(s_TargetVehicle);
                        WriteField(s_WriterA, frame, "A", id, "pathInformation.m_Distance", F(info.m_Distance));
                        WriteField(s_WriterA, frame, "A", id, "pathInformation.m_Duration", F(info.m_Duration));
                        WriteField(s_WriterA, frame, "A", id, "pathInformation.m_TotalCost", F(info.m_TotalCost));
                        WriteField(s_WriterA, frame, "A", id, "pathInformation.m_State", info.m_State.ToString());
                        PathOwner owner = entityManager.GetComponentData<PathOwner>(s_TargetVehicle);
                        WriteField(s_WriterA, frame, "A", id, "pathOwner.m_ElementIndex", owner.m_ElementIndex.ToString(CultureInfo.InvariantCulture));
                        WriteField(s_WriterA, frame, "A", id, "pathOwner.m_State", owner.m_State.ToString());
                    }
                    catch (Exception ex)
                    {
                        CountError("expA.path", ex);
                    }
                }

                try
                {
                    PathfindSetupSystem setup = world.GetExistingSystemManaged<PathfindSetupSystem>();
                    if (setup != null)
                    {
                        WriteField(s_WriterA, frame, "A", "none", "pathfindSetup.pendingSimulationFrame", setup.pendingSimulationFrame.ToString(CultureInfo.InvariantCulture));
                        WriteField(s_WriterA, frame, "A", "none", "pathfindSetup.pendingRequestCount", setup.pendingRequestCount.ToString(CultureInfo.InvariantCulture));
                    }
                }
                catch (Exception ex)
                {
                    CountError("expA.setup", ex);
                }
            }

            Advance(s_Phase == Phase.ABase ? FramesABaseline : FramesAPerturb);
        }

        // ---------------- 实验 B：Blocker + 闭塞交互 ----------------

        private static void SampleB(EntityManager entityManager, World world)
        {
            SimulationSystem simulation = world.GetExistingSystemManaged<SimulationSystem>();
            int frame = simulation != null ? (int)simulation.frameIndex : -1;

            bool sample = s_Phase == Phase.BRead || s_PhaseFrame % IntervalBWrite == 0;
            if (!sample)
            {
                Advance(FramesBWrite);
                return;
            }

            if (s_TargetVehicle == Entity.Null || !entityManager.Exists(s_TargetVehicle) || !entityManager.HasComponent<Blocker>(s_TargetVehicle))
            {
                s_TargetVehicle = FindVehicle(entityManager, new ComponentType[] { ComponentType.ReadOnly<Blocker>(), ComponentType.ReadOnly<TrainCurrentLane>() });
                ModLog.Info("[EXP] B target vehicle = " + Idx(s_TargetVehicle));
            }

            if (s_TargetVehicle == Entity.Null || !entityManager.Exists(s_TargetVehicle))
            {
                CountError("expB.noTarget", null);
                Advance(s_Phase == Phase.BRead ? FramesBRead : FramesBWrite);
                return;
            }

            string id = Idx(s_TargetVehicle);
            float maxDriveSpeed = -1f;
            try
            {
                Blocker blocker = entityManager.GetComponentData<Blocker>(s_TargetVehicle);
                WriteField(s_WriterB, frame, "B", id, "blocker.m_Type", blocker.m_Type.ToString());
                WriteField(s_WriterB, frame, "B", id, "blocker.m_MaxSpeed", blocker.m_MaxSpeed.ToString(CultureInfo.InvariantCulture));

                TrainCurrentLane currentLane = entityManager.GetComponentData<TrainCurrentLane>(s_TargetVehicle);
                s_TargetLane = currentLane.m_Front.m_Lane;
                WriteField(s_WriterB, frame, "B", id, "trainCurrentLane.m_Distance", F(currentLane.m_Distance));
                WriteField(s_WriterB, frame, "B", id, "trainCurrentLane.m_Duration", F(currentLane.m_Duration));
                WriteField(s_WriterB, frame, "B", id, "trainCurrentLane.m_Front.m_Lane", Idx(currentLane.m_Front.m_Lane));
                WriteField(s_WriterB, frame, "B", id, "trainCurrentLane.m_Front.m_CurvePosition", F4(currentLane.m_Front.m_CurvePosition));
                // 车辆侧闭塞/进路状态（TrainLaneFlags：Reserved/BlockReserve/TryReserve/Exclusive/FullReserve/KeepClear…）
                WriteField(s_WriterB, frame, "B", id, "trainCurrentLane.m_Front.m_LaneFlags", currentLane.m_Front.m_LaneFlags.ToString());
                WriteField(s_WriterB, frame, "B", id, "trainCurrentLane.m_Rear.m_LaneFlags", currentLane.m_Rear.m_LaneFlags.ToString());
                WriteField(s_WriterB, frame, "B", id, "trainCurrentLane.m_FrontCache.m_Lane", Idx(currentLane.m_FrontCache.m_Lane));
            }
            catch (Exception ex)
            {
                CountError("expB.target", ex);
            }

            // 本车所在车道上的预留与信号（LaneReservation / LaneSignal 是**车道**组件，不是车辆组件）
            if (s_TargetLane != Entity.Null && entityManager.Exists(s_TargetLane))
            {
                try
                {
                    // 上一轮 lane 侧预留/信号 0 条 —— 这里先记录组件位图，定位"到底缺哪个组件"
                    WriteField(s_WriterB, frame, "B", Idx(s_TargetLane), "lane.hasComponents", LaneComponentBitmap(entityManager, s_TargetLane));
                    if (entityManager.HasComponent<Curve>(s_TargetLane))
                    {
                        WriteField(s_WriterB, frame, "B", Idx(s_TargetLane), "lane.curveLength", F(entityManager.GetComponentData<Curve>(s_TargetLane).m_Length));
                    }

                    if (entityManager.HasComponent<LaneReservation>(s_TargetLane))
                    {
                        LaneReservation reservation = entityManager.GetComponentData<LaneReservation>(s_TargetLane);
                        WriteField(s_WriterB, frame, "B", Idx(s_TargetLane), "laneReservation.m_Blocker", Idx(reservation.m_Blocker));
                        WriteField(s_WriterB, frame, "B", Idx(s_TargetLane), "laneReservation.m_Next", reservation.m_Next.m_Offset + "/" + reservation.m_Next.m_Priority);
                        WriteField(s_WriterB, frame, "B", Idx(s_TargetLane), "laneReservation.m_Prev", reservation.m_Prev.m_Offset + "/" + reservation.m_Prev.m_Priority);
                        WriteField(s_WriterB, frame, "B", Idx(s_TargetLane), "laneReservation.GetPriority", reservation.GetPriority().ToString(CultureInfo.InvariantCulture));
                    }

                    if (entityManager.HasComponent<LaneSignal>(s_TargetLane))
                    {
                        LaneSignal signal = entityManager.GetComponentData<LaneSignal>(s_TargetLane);
                        WriteField(s_WriterB, frame, "B", Idx(s_TargetLane), "laneSignal.m_Signal", signal.m_Signal.ToString());
                        WriteField(s_WriterB, frame, "B", Idx(s_TargetLane), "laneSignal.m_Petitioner", Idx(signal.m_Petitioner));
                        WriteField(s_WriterB, frame, "B", Idx(s_TargetLane), "laneSignal.m_Blocker", Idx(signal.m_Blocker));
                    }
                }
                catch (Exception ex)
                {
                    CountError("expB.lane", ex);
                }
            }

            // 对照：VehicleUtils.GetMaxDriveSpeed(TrainData, TrackLane)（dump:111630）
            try
            {
                string source;
                maxDriveSpeed = ResolveMaxDriveSpeed(entityManager, s_TargetVehicle, s_TargetLane, out source);
                WriteField(s_WriterB, frame, "B", id, "vehicleUtils.GetMaxDriveSpeed", F(maxDriveSpeed));
                WriteField(s_WriterB, frame, "B", id, "vehicleUtils.source", source);
            }
            catch (Exception ex)
            {
                CountError("expB.driveSpeed", ex);
            }

            // 后车观察：所有采样车中，front lane 与本车相同的车（是否进入同一 lane）
            TrySampleFollowers(entityManager, frame, id);

            // 写入窗口
            if (s_Phase == Phase.BWrite && s_WriteAllowed)
            {
                ApplyBlockerWrite(entityManager, frame, id);
            }

            Advance(s_Phase == Phase.BRead ? FramesBRead : FramesBWrite);
        }

        private static void TrySampleFollowers(EntityManager entityManager, int frame, string targetId)
        {
            if (s_TargetLane == Entity.Null)
            {
                return;
            }

            try
            {
                EntityQuery query = entityManager.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<TrainCurrentLane>() });
                NativeArray<Entity> vehicles = query.ToEntityArray(Allocator.Temp);
                try
                {
                    int limit = Math.Min(vehicles.Length, MaxVehicles);
                    for (int i = 0; i < limit; i++)
                    {
                        Entity vehicle = vehicles[i];
                        if (vehicle == s_TargetVehicle || !entityManager.HasComponent<TrainCurrentLane>(vehicle))
                        {
                            continue;
                        }

                        TrainCurrentLane other = entityManager.GetComponentData<TrainCurrentLane>(vehicle);
                        bool sameLane = other.m_Front.m_Lane == s_TargetLane;
                        string otherId = Idx(vehicle);
                        // 上一轮只记"同 lane"的车，结果 0 条；现在记录全部采样车，便于观察队列关系
                        WriteField(s_WriterB, frame, "B", otherId, "other.sameFrontLane", sameLane ? "1" : "0");
                        WriteField(s_WriterB, frame, "B", otherId, "other.frontLane", Idx(other.m_Front.m_Lane));
                        WriteField(s_WriterB, frame, "B", otherId, "other.frontLaneFlags", other.m_Front.m_LaneFlags.ToString());
                        if (sameLane)
                        {
                            WriteField(s_WriterB, frame, "B", otherId, "follower.sameFrontLaneAsTarget", "1");
                        }

                        if (entityManager.HasComponent<Blocker>(vehicle))
                        {
                            Blocker blocker = entityManager.GetComponentData<Blocker>(vehicle);
                            WriteField(s_WriterB, frame, "B", otherId, "follower.blocker.m_MaxSpeed", blocker.m_MaxSpeed.ToString(CultureInfo.InvariantCulture));
                        }

                        WriteField(s_WriterB, frame, "B", otherId, "follower.ofTarget", targetId);
                    }
                }
                finally
                {
                    vehicles.Dispose();
                }
            }
            catch (Exception ex)
            {
                CountError("expB.followers", ex);
            }
        }

        private static void ApplyBlockerWrite(EntityManager entityManager, int frame, string id)
        {
            try
            {
                Blocker blocker = entityManager.GetComponentData<Blocker>(s_TargetVehicle);
                if (!s_HasOriginalBlocker)
                {
                    s_OriginalBlocker = blocker;
                    s_HasOriginalBlocker = true;
                    ModLog.Info("[EXP] B write-phase begin: original m_MaxSpeed=" + blocker.m_MaxSpeed + " m_Type=" + blocker.m_Type + " (entity " + id + ")");
                }

                // 重要：目标值必须由**原值**算出且恒定。若按"当前值"再折半，会被反复折半一路降到 1（车直接停死）
                byte halved = (byte)Math.Max(1, s_OriginalBlocker.m_MaxSpeed / 2);
                if (blocker.m_MaxSpeed != halved)
                {
                    Blocker modified = blocker;
                    modified.m_MaxSpeed = halved;
                    entityManager.SetComponentData(s_TargetVehicle, modified);
                    s_WriteCount++;
                }

                WriteField(s_WriterB, frame, "B", id, "write.applied_m_MaxSpeed", halved.ToString(CultureInfo.InvariantCulture));
                WriteField(s_WriterB, frame, "B", id, "write.writeCount", s_WriteCount.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                CountError("expB.write", ex);
                s_Faulted = true;
                RestoreBlocker(entityManager);
                Finish("write-faulted");
            }
        }

        private static void RestoreBlocker(EntityManager entityManager)
        {
            if (!s_HasOriginalBlocker || s_TargetVehicle == Entity.Null || !entityManager.Exists(s_TargetVehicle))
            {
                return;
            }

            try
            {
                if (entityManager.HasComponent<Blocker>(s_TargetVehicle))
                {
                    entityManager.SetComponentData(s_TargetVehicle, s_OriginalBlocker);
                    ModLog.Info("[EXP] B restored original Blocker (m_MaxSpeed=" + s_OriginalBlocker.m_MaxSpeed + ")");
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("[EXP] restore failed: " + ex);
            }
            finally
            {
                s_HasOriginalBlocker = false;
            }
        }

        // ---------------- 实验 D：玩家指定站台绑定链（Q1–Q6） ----------------

        // ---------------- 阶段 V：V1（站台轨道 LaneReservation）+ V3（单车容量） ----------------

        private static void SampleV(EntityManager entityManager, World world)
        {
            SimulationSystem simulation = world.GetExistingSystemManaged<SimulationSystem>();
            int frame = simulation != null ? (int)simulation.frameIndex : -1;

            SampleV1(entityManager, frame);
            SampleV3(entityManager, frame);

            Advance(FramesV);
        }

        /// <summary>V1：遍历所有站台轨道（TrackLane ∧ TrackLaneFlags.Station），记录是否带 LaneReservation 与占用者。</summary>
        private static void SampleV1(EntityManager entityManager, int frame)
        {
            try
            {
                EntityQuery laneQuery = entityManager.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<NetTrackLane>() });
                EntityQuery trainQuery = entityManager.CreateEntityQuery(new ComponentType[]
                {
                    ComponentType.ReadOnly<VehiclePublicTransport>(),
                    ComponentType.ReadOnly<TrainCurrentLane>()
                });

                NativeArray<Entity> lanes = laneQuery.ToEntityArray(Allocator.Temp);
                NativeArray<Entity> trains = trainQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    int stationLaneTotal = 0;
                    int detailed = 0;
                    int hasReservation = 0;

                    for (int i = 0; i < lanes.Length; i++)
                    {
                        Entity lane = lanes[i];
                        NetTrackLane trackLane = entityManager.GetComponentData<NetTrackLane>(lane);
                        if ((trackLane.m_Flags & Game.Net.TrackLaneFlags.Station) == 0)
                        {
                            continue;
                        }

                        stationLaneTotal++;
                        if (detailed >= MaxStationLanes)
                        {
                            continue;
                        }

                        detailed++;
                        string id = Idx(lane);
                        WriteField(s_WriterV, frame, "V1", id, "v1.platformLane", id);

                        bool has = entityManager.HasComponent<LaneReservation>(lane);
                        WriteField(s_WriterV, frame, "V1", id, "v1.hasLaneReservation", has ? "1" : "0");

                        if (has)
                        {
                            hasReservation++;
                            LaneReservation reservation = entityManager.GetComponentData<LaneReservation>(lane);
                            WriteField(s_WriterV, frame, "V1", id, "v1.blocker", Idx(reservation.m_Blocker));
                            WriteField(s_WriterV, frame, "V1", id, "v1.blockerIsTrain", IsTrain(entityManager, reservation.m_Blocker) ? "1" : "0");
                            WriteField(s_WriterV, frame, "V1", id, "v1.blockerIsSelfTrain", reservation.m_Blocker != Entity.Null && reservation.m_Blocker == FindTrainOnLane(entityManager, trains, lane) ? "1" : "0");
                            WriteField(s_WriterV, frame, "V1", id, "v1.laneReservation.m_Next", reservation.m_Next.m_Offset + "/" + reservation.m_Next.m_Priority);
                            WriteField(s_WriterV, frame, "V1", id, "v1.laneReservation.m_Prev", reservation.m_Prev.m_Offset + "/" + reservation.m_Prev.m_Priority);
                            WriteField(s_WriterV, frame, "V1", id, "v1.laneReservation.priority", reservation.GetPriority().ToString(CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            WriteField(s_WriterV, frame, "V1", id, "v1.blocker", "n/a");
                            WriteField(s_WriterV, frame, "V1", id, "v1.blockerIsTrain", "n/a");
                        }

                        WriteField(s_WriterV, frame, "V1", id, "v1.trainEntity", Idx(FindTrainOnLane(entityManager, trains, lane)));
                    }

                    WriteField(s_WriterV, frame, "V1", "summary", "v1.stationLaneTotal", stationLaneTotal.ToString(CultureInfo.InvariantCulture));
                    WriteField(s_WriterV, frame, "V1", "summary", "v1.stationLaneDetailed", detailed.ToString(CultureInfo.InvariantCulture));
                    WriteField(s_WriterV, frame, "V1", "summary", "v1.stationLaneWithReservation", hasReservation.ToString(CultureInfo.InvariantCulture));
                    WriteField(s_WriterV, frame, "V1", "summary", "v1.trainsWithPublicTransport", trains.Length.ToString(CultureInfo.InvariantCulture));
                }
                finally
                {
                    lanes.Dispose();
                    trains.Dispose();
                }
            }
            catch (Exception ex)
            {
                CountError("expV.v1", ex);
            }
        }

        /// <summary>V3：列车 → PrefabRef → PublicTransportVehicleData（容量/运输类型）。</summary>
        private static void SampleV3(EntityManager entityManager, int frame)
        {
            try
            {
                EntityQuery vehicleQuery = entityManager.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<VehiclePublicTransport>() });
                NativeArray<Entity> vehicles = vehicleQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    WriteField(s_WriterV, frame, "V3", "summary", "v3.vehicleTotal", vehicles.Length.ToString(CultureInfo.InvariantCulture));

                    int limit = Math.Min(vehicles.Length, MaxV3Trains);
                    for (int i = 0; i < limit; i++)
                    {
                        Entity vehicle = vehicles[i];
                        string id = Idx(vehicle);
                        WriteField(s_WriterV, frame, "V3", id, "v3.vehicle", id);

                        Entity prefab = entityManager.HasComponent<PrefabRef>(vehicle)
                            ? entityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab
                            : Entity.Null;
                        WriteField(s_WriterV, frame, "V3", id, "v3.prefab", Idx(prefab));

                        if (prefab != Entity.Null && entityManager.Exists(prefab) && entityManager.HasComponent<PublicTransportVehicleData>(prefab))
                        {
                            PublicTransportVehicleData data = entityManager.GetComponentData<PublicTransportVehicleData>(prefab);
                            WriteField(s_WriterV, frame, "V3", id, "v3.transportType", data.m_TransportType.ToString());
                            WriteField(s_WriterV, frame, "V3", id, "v3.passengerCapacity", data.m_PassengerCapacity.ToString(CultureInfo.InvariantCulture));
                            WriteField(s_WriterV, frame, "V3", id, "v3.purposeMask", Sanitize(data.m_PurposeMask.ToString()));
                            WriteField(s_WriterV, frame, "V3", id, "v3.maintenanceRange", F(data.m_MaintenanceRange));
                        }
                        else
                        {
                            WriteField(s_WriterV, frame, "V3", id, "v3.transportType", "missing");
                            WriteField(s_WriterV, frame, "V3", id, "v3.passengerCapacity", "missing");
                        }

                        // m_SizeClass 不在 PublicTransportVehicleData / TrainData 上（dump 已核实）；CarData 上有
                        string sizeClass = "n/a";
                        if (prefab != Entity.Null && entityManager.Exists(prefab) && entityManager.HasComponent<CarData>(prefab))
                        {
                            sizeClass = entityManager.GetComponentData<CarData>(prefab).m_SizeClass.ToString();
                        }

                        WriteField(s_WriterV, frame, "V3", id, "v3.sizeClass", sizeClass);
                        WriteField(s_WriterV, frame, "V3", id, "v3.sizeClassNote", "PublicTransportVehicleData/TrainData 无 m_SizeClass");
                    }
                }
                finally
                {
                    vehicles.Dispose();
                }
            }
            catch (Exception ex)
            {
                CountError("expV.v3", ex);
            }
        }

        private static bool IsTrain(EntityManager entityManager, Entity entity)
        {
            return entity != Entity.Null && entityManager.Exists(entity) && entityManager.HasComponent<Game.Vehicles.Train>(entity);
        }

        private static Entity FindTrainOnLane(EntityManager entityManager, NativeArray<Entity> trains, Entity lane)
        {
            for (int i = 0; i < trains.Length; i++)
            {
                Entity train = trains[i];
                if (!entityManager.Exists(train) || !entityManager.HasComponent<TrainCurrentLane>(train))
                {
                    continue;
                }

                TrainCurrentLane currentLane = entityManager.GetComponentData<TrainCurrentLane>(train);
                if (currentLane.m_Front.m_Lane == lane || currentLane.m_Rear.m_Lane == lane || currentLane.m_FrontCache.m_Lane == lane)
                {
                    return train;
                }
            }

            return Entity.Null;
        }

        private static string Sanitize(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Replace(',', ';');
        }

        private static void SampleD(EntityManager entityManager, World world)
        {
            SimulationSystem simulation = world.GetExistingSystemManaged<SimulationSystem>();
            int frame = simulation != null ? (int)simulation.frameIndex : -1;

            // 一次性装载快照（Q1/Q2/Q5 基线）+ 周期快照（Q6：玩家中途改站台可被捕获）
            if (!s_BindingInstallDumped)
            {
                s_BindingInstallDumped = true;
                DumpBindingSnapshot(entityManager, frame, "install");
            }

            if (s_PhaseFrame % IntervalDSnapshot == 0)
            {
                s_BindingSnapshot++;
                DumpBindingSnapshot(entityManager, frame, "snap" + s_BindingSnapshot);
            }

            // Q3/Q4：逐帧跟踪正在运行的列车
            try
            {
                EntityQuery query = entityManager.CreateEntityQuery(new ComponentType[]
                {
                    ComponentType.ReadOnly<TrainCurrentLane>(),
                    ComponentType.ReadOnly<PathInformation>()
                });

                NativeArray<Entity> trains = query.ToEntityArray(Allocator.Temp);
                try
                {
                    int limit = Math.Min(trains.Length, MaxBindingTrains);
                    bool dumpNavLanes = s_PhaseFrame % NavLaneDumpInterval == 0;

                    for (int i = 0; i < limit; i++)
                    {
                        Entity train = trains[i];
                        string id = Idx(train);

                        PathInformation info = entityManager.GetComponentData<PathInformation>(train);
                        WriteField(s_WriterD, frame, "D", id, "pathInformation.m_Origin", Idx(info.m_Origin));
                        WriteField(s_WriterD, frame, "D", id, "pathInformation.m_Destination", Idx(info.m_Destination));
                        WriteField(s_WriterD, frame, "D", id, "pathInformation.m_Distance", F(info.m_Distance));
                        WriteField(s_WriterD, frame, "D", id, "pathInformation.m_Duration", F(info.m_Duration));
                        WriteField(s_WriterD, frame, "D", id, "pathInformation.m_TotalCost", F(info.m_TotalCost));
                        WriteField(s_WriterD, frame, "D", id, "pathInformation.m_Methods", info.m_Methods.ToString());
                        WriteField(s_WriterD, frame, "D", id, "pathInformation.m_State", info.m_State.ToString());
                        WriteField(s_WriterD, frame, "D", id, "q3.destinationIsStationTrack", IsStationTrack(entityManager, info.m_Destination) ? "1" : "0");
                        WriteField(s_WriterD, frame, "D", id, "q3.destinationBitmap", EntityBitmap(entityManager, info.m_Destination));

                        TrainCurrentLane currentLane = entityManager.GetComponentData<TrainCurrentLane>(train);
                        WriteField(s_WriterD, frame, "D", id, "trainCurrentLane.m_Front.m_Lane", Idx(currentLane.m_Front.m_Lane));
                        WriteField(s_WriterD, frame, "D", id, "q3.frontLaneIsStationTrack", IsStationTrack(entityManager, currentLane.m_Front.m_Lane) ? "1" : "0");
                        WriteField(s_WriterD, frame, "D", id, "trainCurrentLane.m_Front.m_LaneFlags", currentLane.m_Front.m_LaneFlags.ToString());

                        if (entityManager.HasComponent<TrainNavigation>(train))
                        {
                            WriteField(s_WriterD, frame, "D", id, "trainNavigation.m_Speed", F(entityManager.GetComponentData<TrainNavigation>(train).m_Speed));
                        }

                        if (entityManager.HasComponent<Odometer>(train))
                        {
                            WriteField(s_WriterD, frame, "D", id, "odometer.m_Distance", F(entityManager.GetComponentData<Odometer>(train).m_Distance));
                        }

                        // Q4：TrainNavigationLane buffer 与 m_Destination 的关系
                        int navLength = TryGetBufferLength<TrainNavigationLane>(entityManager, train);
                        WriteField(s_WriterD, frame, "D", id, "trainNavigationLane.length", navLength.ToString(CultureInfo.InvariantCulture));

                        if (navLength > 0)
                        {
                            DynamicBuffer<TrainNavigationLane> nav = entityManager.GetBuffer<TrainNavigationLane>(train, true);
                            Entity lastLane = nav[navLength - 1].m_Lane;
                            WriteField(s_WriterD, frame, "D", id, "trainNavigationLane.last", Idx(lastLane));
                            WriteField(s_WriterD, frame, "D", id, "q4.lastEqualsDestination", lastLane == info.m_Destination ? "1" : "0");
                            WriteField(s_WriterD, frame, "D", id, "q4.lastIsStationTrack", IsStationTrack(entityManager, lastLane) ? "1" : "0");
                            WriteField(s_WriterD, frame, "D", id, "q4.anyEntryIsDestination", ContainsLane(nav, info.m_Destination) ? "1" : "0");

                            if (dumpNavLanes)
                            {
                                int shown = Math.Min(navLength, MaxNavLanesShown);
                                for (int k = 0; k < shown; k++)
                                {
                                    WriteField(s_WriterD, frame, "D", id,
                                        "trainNavigationLane[" + k + "]",
                                        Idx(nav[k].m_Lane) + "|cp=" + F2(nav[k].m_CurvePosition) + "|flags=" + nav[k].m_Flags + "|station=" + (IsStationTrack(entityManager, nav[k].m_Lane) ? "1" : "0"));
                                }
                            }
                        }
                    }
                }
                finally
                {
                    trains.Dispose();
                }
            }
            catch (Exception ex)
            {
                CountError("expD.trains", ex);
            }

            Advance(FramesD);
        }

        /// <summary>装载快照：Q1（PathTargets 挂哪）、Q2（Start/EndLane 指向）、Q5（RouteWaypoint 指向的实体组件）。</summary>
        private static void DumpBindingSnapshot(EntityManager entityManager, int frame, string tag)
        {
            try
            {
                EntityQuery hostQuery = entityManager.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<PathTargets>() });
                NativeArray<Entity> hosts = hostQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    WriteField(s_WriterD, frame, "D", "summary", "snapshot." + tag + ".pathTargetsHosts", hosts.Length.ToString(CultureInfo.InvariantCulture));
                    int limit = Math.Min(hosts.Length, MaxPathTargetsHosts);
                    for (int i = 0; i < limit; i++)
                    {
                        Entity host = hosts[i];
                        string id = Idx(host);
                        PathTargets targets = entityManager.GetComponentData<PathTargets>(host);

                        WriteField(s_WriterD, frame, "D", id, "q1.pathTargetsHostBitmap", EntityBitmap(entityManager, host));
                        WriteField(s_WriterD, frame, "D", id, "pathTargets.m_StartLane", Idx(targets.m_StartLane));
                        WriteField(s_WriterD, frame, "D", id, "pathTargets.m_EndLane", Idx(targets.m_EndLane));
                        WriteField(s_WriterD, frame, "D", id, "pathTargets.m_CurvePositions", F2(targets.m_CurvePositions));
                        WriteField(s_WriterD, frame, "D", id, "pathTargets.m_ReadyStartPosition", F3(targets.m_ReadyStartPosition));
                        WriteField(s_WriterD, frame, "D", id, "pathTargets.m_ReadyEndPosition", F3(targets.m_ReadyEndPosition));
                        WriteField(s_WriterD, frame, "D", id, "q2.startLaneIsStationTrack", IsStationTrack(entityManager, targets.m_StartLane) ? "1" : "0");
                        WriteField(s_WriterD, frame, "D", id, "q2.endLaneIsStationTrack", IsStationTrack(entityManager, targets.m_EndLane) ? "1" : "0");
                        WriteField(s_WriterD, frame, "D", id, "q2.startLaneBitmap", EntityBitmap(entityManager, targets.m_StartLane));
                        WriteField(s_WriterD, frame, "D", id, "q2.endLaneBitmap", EntityBitmap(entityManager, targets.m_EndLane));
                    }
                }
                finally
                {
                    hosts.Dispose();
                }
            }
            catch (Exception ex)
            {
                CountError("expD.pathTargets", ex);
            }

            try
            {
                EntityQuery routeQuery = entityManager.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<TransportLine>() });
                NativeArray<Entity> routes = routeQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    WriteField(s_WriterD, frame, "D", "summary", "snapshot." + tag + ".transportLines", routes.Length.ToString(CultureInfo.InvariantCulture));
                    int routeLimit = Math.Min(routes.Length, MaxRoutes);
                    for (int r = 0; r < routeLimit; r++)
                    {
                        Entity route = routes[r];
                        string routeId = Idx(route);
                        int waypointCount = TryGetBufferLength<RouteWaypoint>(entityManager, route);
                        WriteField(s_WriterD, frame, "D", routeId, "q5.routeWaypointCount", waypointCount.ToString(CultureInfo.InvariantCulture));

                        if (waypointCount <= 0)
                        {
                            continue;
                        }

                        DynamicBuffer<RouteWaypoint> waypoints = entityManager.GetBuffer<RouteWaypoint>(route, true);
                        int shown = Math.Min(waypointCount, MaxWaypointsPerRoute);
                        for (int w = 0; w < shown; w++)
                        {
                            Entity waypoint = waypoints[w].m_Waypoint;
                            string waypointId = Idx(waypoint);
                            string detail = "bitmap=" + EntityBitmap(entityManager, waypoint);

                            if (waypoint != Entity.Null && entityManager.Exists(waypoint))
                            {
                                if (entityManager.HasComponent<Waypoint>(waypoint))
                                {
                                    detail += "|index=" + entityManager.GetComponentData<Waypoint>(waypoint).m_Index;
                                }

                                if (entityManager.HasComponent<PathTargets>(waypoint))
                                {
                                    PathTargets t = entityManager.GetComponentData<PathTargets>(waypoint);
                                    detail += "|hasPathTargets=1|endLane=" + Idx(t.m_EndLane)
                                        + "|endIsStation=" + (IsStationTrack(entityManager, t.m_EndLane) ? "1" : "0");
                                }
                                else
                                {
                                    detail += "|hasPathTargets=0";
                                }
                            }

                            WriteField(s_WriterD, frame, "D", routeId + "->" + waypointId, "q5.waypoint[" + w + "]", detail);
                        }
                    }
                }
                finally
                {
                    routes.Dispose();
                }
            }
            catch (Exception ex)
            {
                CountError("expD.routes", ex);
            }
        }

        private static bool ContainsLane(DynamicBuffer<TrainNavigationLane> nav, Entity lane)
        {
            if (lane == Entity.Null)
            {
                return false;
            }

            for (int i = 0; i < nav.Length; i++)
            {
                if (nav[i].m_Lane == lane)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsStationTrack(EntityManager entityManager, Entity entity)
        {
            if (entity == Entity.Null || !entityManager.Exists(entity) || !entityManager.HasComponent<NetTrackLane>(entity))
            {
                return false;
            }

            NetTrackLane trackLane = entityManager.GetComponentData<NetTrackLane>(entity);
            return (trackLane.m_Flags & Game.Net.TrackLaneFlags.Station) != 0;
        }

        /// <summary>实体组件位图（带缓存，避免逐帧重复探测）。</summary>
        private static string EntityBitmap(EntityManager entityManager, Entity entity)
        {
            if (entity == Entity.Null)
            {
                return "null";
            }

            string cached;
            if (s_EntityBitmapCache.TryGetValue(entity, out cached))
            {
                return cached;
            }

            string result;
            if (!entityManager.Exists(entity))
            {
                result = "missing";
            }
            else
            {
                StringBuilder builder = new StringBuilder();
                AppendFlag(builder, "TransportLine", entityManager.HasComponent<TransportLine>(entity));
                AppendFlag(builder, "Route", entityManager.HasComponent<Route>(entity));
                AppendFlag(builder, "Waypoint", entityManager.HasComponent<Waypoint>(entity));
                AppendFlag(builder, "RouteWaypointBuf", TryGetBufferLength<RouteWaypoint>(entityManager, entity) > 0);
                AppendFlag(builder, "TransportStop", entityManager.HasComponent<Game.Routes.TransportStop>(entity));
                AppendFlag(builder, "TrainStop", entityManager.HasComponent<Game.Routes.TrainStop>(entity));
                AppendFlag(builder, "TransportStation", entityManager.HasComponent<Game.Buildings.TransportStation>(entity));
                AppendFlag(builder, "TrackLane", entityManager.HasComponent<NetTrackLane>(entity));
                AppendFlag(builder, "SubLaneBuf", TryGetBufferLength<Game.Net.SubLane>(entityManager, entity) > 0);
                AppendFlag(builder, "Curve", entityManager.HasComponent<Curve>(entity));
                AppendFlag(builder, "Owner", entityManager.HasComponent<Owner>(entity));
                AppendFlag(builder, "PrefabRef", entityManager.HasComponent<PrefabRef>(entity));
                AppendFlag(builder, "PathTargets", entityManager.HasComponent<PathTargets>(entity));
                AppendFlag(builder, "Transform", entityManager.HasComponent<Game.Objects.Transform>(entity));

                if (entityManager.HasComponent<NetTrackLane>(entity))
                {
                    NetTrackLane trackLane = entityManager.GetComponentData<NetTrackLane>(entity);
                    builder.Append("|trackFlags=").Append(trackLane.m_Flags);
                    builder.Append("|isStation=").Append((trackLane.m_Flags & Game.Net.TrackLaneFlags.Station) != 0 ? "1" : "0");
                    builder.Append("|speedLimit=").Append(F(trackLane.m_SpeedLimit));
                }

                result = builder.Length == 0 ? "none" : builder.ToString();
            }

            s_EntityBitmapCache[entity] = result;
            return result;
        }

        private static string F2(Unity.Mathematics.float2 value)
        {
            return F(value.x) + "|" + F(value.y);
        }

        private static string F3(Unity.Mathematics.float3 value)
        {
            return F(value.x) + "|" + F(value.y) + "|" + F(value.z);
        }

        /// <summary>buffer 长度；实体不存在或无该 buffer 返回 -1。</summary>
        private static int TryGetBufferLength<T>(EntityManager entityManager, Entity entity)
            where T : unmanaged, IBufferElementData
        {
            if (entity == Entity.Null || !entityManager.Exists(entity) || !entityManager.HasComponent<T>(entity))
            {
                return -1;
            }

            return entityManager.GetBuffer<T>(entity, true).Length;
        }

        // ---------------- 阶段推进 ----------------

        private static void Advance(int budget)
        {
            s_PhaseFrame++;
            if (s_PhaseFrame < budget)
            {
                return;
            }

            switch (s_Phase)
            {
                case Phase.V:
                    ModLog.Info("[EXP] phase V done (" + budget + " frames)");
                    s_Phase = Phase.D;
                    break;
                case Phase.D:
                    ModLog.Info("[EXP] phase D done (" + budget + " frames)");
                    s_Phase = Phase.C;
                    break;
                case Phase.C:
                    ModLog.Info("[EXP] phase C done (" + budget + " frames)");
                    s_Phase = Phase.ABase;
                    break;
                case Phase.ABase:
                    ModLog.Info("[EXP] phase A-base done (" + budget + " frames)");
                    if (s_WriteAllowed)
                    {
                        s_Phase = Phase.APerturb;
                        ModLog.Info("[EXP] phase A-perturb begin (write Blocker.m_MaxSpeed as cost/speed disturbance)");
                    }
                    else
                    {
                        ModLog.Info("[EXP] phase A-perturb SKIPPED (read-only mode)");
                        s_Phase = Phase.BRead;
                    }
                    break;
                case Phase.APerturb:
                    ModLog.Info("[EXP] phase A-perturb done (" + budget + " frames)");
                    RestoreBlockerAfterA();
                    s_Phase = Phase.BRead;
                    break;
                case Phase.BRead:
                    ModLog.Info("[EXP] phase B-read done (" + budget + " frames)");
                    s_Phase = Phase.BWrite;
                    break;
                case Phase.BWrite:
                    ModLog.Info("[EXP] phase B-write done (" + budget + " frames)");
                    Finish("completed");
                    return;
            }

            s_PhaseFrame = 0;
        }

        private static void RestoreBlockerAfterA()
        {
            // A 的扰动只改 m_MaxSpeed，B 阶段会重新接管；这里保持写入状态不做回写（B 结束时统一回写）
        }

        private static void Finish(string reason)
        {
            s_Phase = Phase.Finished;
            CloseWriters();
            try
            {
                if (!string.IsNullOrEmpty(s_DoneFlag))
                {
                    File.WriteAllText(s_DoneFlag, "done " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " reason=" + reason + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn("[EXP] cannot write done flag: " + ex.GetType().Name);
            }

            ModLog.Info("[EXP] finished reason=" + reason
                + " fieldErrors=" + s_FieldErrors.Count
                + " writeCount=" + s_WriteCount);
            foreach (KeyValuePair<string, int> pair in s_FieldErrors)
            {
                ModLog.Warn("[EXP] field error '" + pair.Key + "' x" + pair.Value);
            }
        }

        private static void CloseWriters()
        {
            Close(ref s_WriterV);
            Close(ref s_WriterD);
            Close(ref s_WriterC);
            Close(ref s_WriterA);
            Close(ref s_WriterB);
        }

        private static void Close(ref StreamWriter writer)
        {
            if (writer == null)
            {
                return;
            }

            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch (Exception)
            {
            }

            writer = null;
        }

        // ---------------- 工具 ----------------

        private static void WriteField(StreamWriter writer, int frame, string experiment, string entity, string field, string value)
        {
            WriteRow(writer, frame, experiment, entity, field, value);
        }

        private static void WriteRow(StreamWriter writer, int frame, string experiment, string entity, string field, string value)
        {
            if (writer == null)
            {
                return;
            }

            writer.Write(frame.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(experiment);
            writer.Write(',');
            writer.Write(entity);
            writer.Write(',');
            writer.Write(field);
            writer.Write(',');
            writer.Write(value);
            writer.Write('\n');
        }

        private static void CountError(string key, Exception ex)
        {
            int count;
            s_FieldErrors.TryGetValue(key, out count);
            s_FieldErrors[key] = count + 1;
            if (count == 0)
            {
                ModLog.Warn("[EXP] sample error '" + key + "': " + (ex == null ? "missing data" : ex.GetType().Name + " " + ex.Message));
            }
        }

        private static Entity FindVehicle(EntityManager entityManager, ComponentType[] components)
        {
            try
            {
                EntityQuery query = entityManager.CreateEntityQuery(components);
                NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
                try
                {
                    return entities.Length > 0 ? entities[0] : Entity.Null;
                }
                finally
                {
                    entities.Dispose();
                }
            }
            catch (Exception ex)
            {
                CountError("findVehicle", ex);
                return Entity.Null;
            }
        }

        /// <summary>
        /// 多路对照（上一轮全部返回 -1，说明 front lane 上没有 TrackLane）：
        ///   1) front lane 自身有 TrackLane → 直接用（权威：dump:111630）
        ///   2) 否则取全局第一条 TrackLane 作参考 → 得到"车限速 × 道限速 × 曲率"的量级
        ///   3) 都不行 → -1 并给出 reason（写入 vehicleUtils.source 字段）
        /// </summary>
        private static float ResolveMaxDriveSpeed(EntityManager entityManager, Entity vehicle, Entity lane, out string source)
        {
            source = "none";

            Entity prefab = entityManager.HasComponent<PrefabRef>(vehicle)
                ? entityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab
                : Entity.Null;
            if (prefab == Entity.Null || !entityManager.Exists(prefab) || !entityManager.HasComponent<TrainData>(prefab))
            {
                source = "noTrainData";
                return -1f;
            }

            TrainData trainData = entityManager.GetComponentData<TrainData>(prefab);

            if (lane != Entity.Null && entityManager.Exists(lane) && entityManager.HasComponent<NetTrackLane>(lane))
            {
                source = "frontLane";
                return VehicleUtils.GetMaxDriveSpeed(trainData, entityManager.GetComponentData<NetTrackLane>(lane));
            }

            try
            {
                EntityQuery query = entityManager.CreateEntityQuery(new ComponentType[] { ComponentType.ReadOnly<NetTrackLane>() });
                NativeArray<Entity> lanes = query.ToEntityArray(Allocator.Temp);
                try
                {
                    if (lanes.Length > 0)
                    {
                        source = "refTrackLane:" + Idx(lanes[0]);
                        return VehicleUtils.GetMaxDriveSpeed(trainData, entityManager.GetComponentData<NetTrackLane>(lanes[0]));
                    }
                }
                finally
                {
                    lanes.Dispose();
                }
            }
            catch (Exception)
            {
            }

            source = "noTrackLaneAnywhere";
            return -1f;
        }

        /// <summary>车道组件位图：定位"哪个组件缺席导致读不到闭塞/信号"。</summary>
        private static string LaneComponentBitmap(EntityManager entityManager, Entity lane)
        {
            StringBuilder builder = new StringBuilder();
            AppendFlag(builder, "Curve", entityManager.HasComponent<Curve>(lane));
            AppendFlag(builder, "Lane", entityManager.HasComponent<Game.Net.Lane>(lane));
            AppendFlag(builder, "TrackLane", entityManager.HasComponent<NetTrackLane>(lane));
            AppendFlag(builder, "ConnectionLane", entityManager.HasComponent<Game.Net.ConnectionLane>(lane));
            AppendFlag(builder, "LaneReservation", entityManager.HasComponent<LaneReservation>(lane));
            AppendFlag(builder, "LaneSignal", entityManager.HasComponent<LaneSignal>(lane));
            return builder.Length == 0 ? "none" : builder.ToString().TrimEnd(',');
        }

        private static void AppendFlag(StringBuilder builder, string name, bool present)
        {
            if (present)
            {
                builder.Append(name).Append(',');
            }
        }

        private static string Idx(Entity entity)
        {
            return entity == Entity.Null ? "null" : entity.Index + ":" + entity.Version;
        }

        private static string F(float value)
        {
            return value.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static string F3(float value)
        {
            return value.ToString("F3", CultureInfo.InvariantCulture);
        }

        private static string F9(float value)
        {
            return value.ToString("F9", CultureInfo.InvariantCulture);
        }

        private static string F4(Unity.Mathematics.float4 value)
        {
            return F(value.x) + "|" + F(value.y) + "|" + F(value.z) + "|" + F(value.w);
        }
    }
}
