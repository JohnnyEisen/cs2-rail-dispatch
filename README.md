# Rail Capacity Guard（`cs2-rail-dispatch`）

《城市：天际线 2》(Cities: Skylines II) 铁路调度 Mod。程序集与命名空间为 `RailCapacityGuard`。

针对原版铁路运行的两个痛点：**咽喉区（throat）互锁** 与 **发车间隔 / 图定时钟**。
设计原则：**读原版数据、不拦原版函数**；只有发车帧与两个默认关闭的实验开关会写数据，写过的原版字段都会缓存基线并在关开关 / 读档 / 卸载时恢复。

> **Beta（开发中，未完成）** — 版本 `0.1.0-beta`。功能按阶段（P0–P8）迭代，设置项、行为与存档兼容性都可能随时变化；**不建议用于长期存档**，启用前先备份。目标游戏版本 **1.6.2f1**。

## 功能

| 阶段 | 位置 | 作用 | 默认 |
|------|------|------|------|
| P0–P1 | `Mod.cs`、`Services/GameApiProbe.cs`、`RailTimebaseSystem.cs` | 骨架、反射 API 探测（缺成员即记录），帧 ↔ 游戏分钟换算 | 开 |
| P2 | `Services/CapacityService.cs` | 站台容量判断：读站台轨道 `LaneReservation`，判断站台是否被占 | 开 |
| P3 | `Services/ThroatZoneService.cs` | 咽喉区协调：把咽喉 lane 分组，`IsBusy` 只把**停驻**的挡路车算忙（移动中的顷刻腾出，不算） | 关 |
| P4 | `Services/PathfindCostService.cs` | 动态寻路代价：缩放 `Game.Prefabs.PathfindTrackData` 的 Comfort 维度（道岔 / 交叉 / 对向 / 急弯）。缓存原版值 → 应用 → 每 256 帧对账；读档、关开关、卸载均恢复原版值 | 关 |
| P6-A | `TrainTooltipSystem.cs` | 列车 tooltip 第 4 行 DMI：允许速度（`Blocker.m_MaxSpeed`）、前方信号（`LaneSignal`）、挡路者、本车速度。悬浮时实时读，缺数据直接省略，不猜 | 开 |
| P7 | `TimetableDispatchSystem.cs`、`Services/SegmentTimeService.cs` | 时刻表调度主体：发车帧写入（照搬 TT 语义）、Hold/Depart 决策、本段 ETA 与进度（`PathOwner.m_ElementIndex`）；区间时间三级回退（本车上一段实测 leg → 线路真实 leg 中位数 → 原版 `VehicleTiming.m_AverageTravelTime`）；单位（route units ÷60）与 `uint` 回绕护栏（`kMaxLegFrames = 262144` 帧 = 1 游戏日） | 开 |
| P8 | 同上 | 基准时刻表导出（每线每会话一次，写段运行 / 停站中位数与循环时间估算）；限速探针（写 `Blocker.m_MaxSpeed`，`byte/5 = m/s`） | 关 |

## 设置（Main 标签，General / Timetable 两组）

开关：

| 设置 | 默认 | 说明 |
|------|------|------|
| `EnableDiagnosticLogging` | 关 | Verbose 日志（排查用） |
| `EnableTimetableDispatch` | 开 | P7 主体开关 |
| `EnableCapacityFeedback` | 开 | P2 站台容量反馈 |
| `EnableThroatCoordination` | 关 | P3 咽喉区协调 |
| `EnableEarlyDeparture` | 开 | 允许按运行时间比例提前发车 |
| `EnableClockMeasurement` | 关 | 时钟测量 |
| `EnableTooltip` / `EnableDmiDisplay` | 开 / 开 | tooltip 与 DMI 第 4 行 |
| `EnablePathfindCostScale` | 关 | P4 总开关（关闭时不写任何寻路数据） |
| `EnableTimetableExport` | 关 | P8 基准时刻表导出 |
| `EnableSpeedControlProbe` | 关 | P8 限速探针（接管管理车辆允许速度） |
| `EnableFleetAdaptation` | **硬性关闭** | getter 恒返回 false：历史版本写出的车队值不可信，写入路径不再执行 |

滑块：`PostponeStepFrames`(16)、`SafetyMarginFrames`(120)、`DiagnosticIntervalFrames`(4096)、`MaxHoldMinutes`(0=自动)、`MinHeadwayMinutes`(2)、`MaxEarlyPercent`(20)、`MaxBoardingMinutes`(180，卡住乘客的停站硬上限)、`PathfindSwitchCostScale` / `PathfindCurveCostScale`(1=原版)、`SpeedControlProbeKmh`(0=不写)。

## 环境要求

- Cities: Skylines II **1.6.2f1**（Desktop）
- .NET SDK（`net48`，`LangVersion 9.0`）
- 游戏安装目录的 `Cities2_Data/Managed`（工程以 `HintPath` 引用 `Game.dll`、`Colossal.*`、`Unity.*`、`mscorlib` 等，全部 `Private=false`，**绝不随 Mod 分发游戏程序集**）
- 目标 Mods 目录：`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\RailCapacityGuard\`

## 构建

```powershell
# 故意不导入官方 ModdingToolchain（Mod.props / Mod.targets）：
# Mod.targets 的 DeployWIP 会 RemoveDir 目标目录，Mod.props 的 LocalModsPath 缺省会退化成盘根路径。
dotnet build "Rail Capacity Guard.csproj" -c Debug `
  -p:GameManagedDir="<游戏安装目录>\Cities2_Data\Managed"
```

产物：`bin/Debug/net48/RailCapacityGuard.dll`。

本机路径（`GameManagedDir` / `ModDeployDir` / `LocalModsPath`）不入库：把 [`Directory.Build.props.example`](Directory.Build.props.example) 复制成 `Directory.Build.props` 填好即可（该文件已被 `.gitignore` 忽略），也可以每次用 `-p:` 传。`GameManagedDir` 缺失时构建会明确报错，不会静默降级。

## 部署

1. **先完全关闭游戏** —— CS2 运行中替换 Mod DLL 会失败或读不到新程序集
2. `dotnet build` 结束后 `DeployToGameMods` / `DeployUIBundle` 目标会自动把 `RailCapacityGuard.dll`、`.pdb`、`0Harmony.dll` 与 UI bundle 拷到 `ModDeployDir`（默认 `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\RailCapacityGuard\`，可用 `-p:ModDeployDir=` 改）
3. 不想自动部署时：把 `ModDeployDir` 指向临时目录，或手动拷 `bin/Debug/net48/RailCapacityGuard.dll`
4. 启动游戏，确认 playset 中本 Mod 已启用
5. 日志：`...\Logs\RailCapacityGuard.log`（需在设置里打开 `EnableDiagnosticLogging` 才有 Verbose 行）

UI 探针（React + webpack，当前仅为 hello-world，未接入玩法）：

```powershell
cd UI; npm ci; npm run build   # 输出 UI/build/RailCapacityGuard.mjs
```

## 目录结构

```
Mod.cs                      Mod 入口：设置注册、系统注册、locale 注册
Setting.cs                  设置面板（Main / General + Timetable）
TimetableDispatchSystem.cs  P7 主体：发车决策、ETA、进度、窗口统计
TrainTooltipSystem.cs       列车 tooltip（含 P6-A DMI 第 4 行）
RailTimebaseSystem.cs       帧 ↔ 游戏分钟 / route units 换算
LineDiagnosticsSystem.cs    线路诊断
SaveLoadHooksSystem.cs      IPre/PostDeserialize 的唯一落点
ExperimentSamplerSystem.cs  实验采样宿主（当前未注册到调度）
Components/                 ECS 组件（StationClassData）
Runtime/                    运行时状态（VehicleSchedule / LineRuntimeState / VehicleTooltipInfo）
Services/                   CapacityService / ThroatZoneService / PathfindCostService /
                            SegmentTimeService / StationResolverService / FleetPolicyService /
                            GameApiProbe / ServiceRegistry …
Utils/                      ModLog / UnitConversion / RailGuardLocaleSource / ExperimentSampler
Patches/                    Harmony 补丁目录（当前无补丁，仅目录约定）
UI/                         独立 UI 探针（webpack 构建，产物不进版本库）
```

## 已知问题与注意

- **写入面**：只有 P7 发车帧、P4 寻路代价（默认关）、P8 限速探针（默认关）会写原版数据；其余全为只读。`EnableFleetAdaptation` 硬性关闭。
- **内存需求（载图期）**：载入城市时游戏会同时申请地形 / 纹理 / 批处理材质，地图类 Mod（例如解锁全图的 529 Tiles）会把峰值成倍放大。**16 GB 内存 + 大量 Mod** 的组合容易在载图进度条阶段耗尽虚拟内存，表现为长时间卡死，或 `Player.log` 里出现 `Could not allocate memory: System out of memory!`（通常紧接 `ManagedBatchSystem:CreateMaterial -> TextureAsset:LoadData` 栈，即原生分配失败）。
  - 排查：`Player.log` 搜 `Could not allocate memory`；事件查看器 → Windows 日志 → 系统 → 来源 `Microsoft-Windows-Resource-Exhaustion-Detector`（事件 2004）会列出占用虚拟内存最大的进程。
  - 缓解（按性价比）：关闭常驻内存大户 → 页面文件改为固定且足够大 → 关闭 / 减少地图类 Mod、降低纹理质量 → 加内存到 32 GB。
- **未实现**：Harmony 补丁（`Patches/` 为空，`Lib.Harmony` 仅为预留引用）、Paradox Mods 发布流程、车队自适应写入。

## 鸣谢

- **TransitTimetables (TT)** — AmicusDeus，MIT。P7 的发车帧写入语义照搬其实现（`TimetableDispatchSystem.cs` 注释中标明对应行号）。第三方许可原文见 [`LICENSE-TRANSITTIMETABLES.txt`](LICENSE-TRANSITTIMETABLES.txt)。
- **Traffic Tool Essentials (TTE)** — 程序集 `C2VM.TrafficToolEssentials`。P4 的「缓存原版值 → 应用 → 看门狗 + 读档恢复」三段式与其 `PathfindCostModifierSystem` 同构；P2/P3 的「读数据、不拦函数」思路亦参考该项目。仅思路与结构参考，未拷贝代码。
- **RealisticPathFinding (RPF)** — ruzbeh0（<https://github.com/ruzbeh0/RealisticPathFinding>）。P2/P3 的「读数据、不拦函数」思路参考。
- **ExtendedTooltip** — 工具提示的 children 结构与挂载方式参考（<https://thunderstore.io/c/cities-skylines-ii/p/Cities2Modding/ExtendedTooltip/>）；未使用其 Harmony 路线，未拷贝代码。
- **游戏本体与运行时** — 《城市：天际线 2》© Colossal Order / Paradox Interactive。`Game.*`、`Colossal.*`、`Unity.*` 运行时由游戏提供，本仓库不分发。
- 除 TransitTimetables 外，以上均为**思路 / 结构参考**，不含第三方代码。

## 许可证

- 本仓库代码：MIT，见 [`LICENSE`](LICENSE)（Copyright (c) 2026 JohnnyEisen）
- 第三方：TransitTimetables 的 MIT 许可与版权声明保留在 [`LICENSE-TRANSITTIMETABLES.txt`](LICENSE-TRANSITTIMETABLES.txt)（Copyright (c) 2026 AmicusDeus）
