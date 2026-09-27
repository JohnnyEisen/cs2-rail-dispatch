using System;
using Colossal.IO.AssetDatabase;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Serialization;
using RailCapacityGuard.Services;
using RailCapacityGuard.Utils;
using Unity.Entities;

namespace RailCapacityGuard
{
    /// <summary>
    /// Mod 入口。
    ///
    /// 关于 [FileVersion] / [ModVersion] 特性：**游戏程序集内不存在这两个特性**
    /// （已核对 _analysis/game_api_full.txt 全量 112785 行，FileVersion / ModVersion
    /// 零命中；上一版可运行的 Mod 也未使用任何程序集级特性）。
    /// 因此这里不写不存在的 API，版本信息统一由 csproj 的
    /// Version / AssemblyVersion / FileVersion 生成，运行时读 Assembly.GetName().Version。
    /// </summary>
    public class Mod : IMod
    {
        public static ServiceRegistry Services { get; private set; }

        /// <summary>设置面板实例（持久化完全由游戏负责，本 Mod 不写自己的存档逻辑）。</summary>
        public static RailCapacityGuardSetting Settings { get; private set; }

        public static string Version => typeof(Mod).Assembly.GetName().Version?.ToString() ?? "unknown";

        public void OnLoad(UpdateSystem updateSystem)
        {
            ModLog.Initialize();
            ModLog.Info(nameof(Mod) + "." + nameof(OnLoad) + " v" + Version);

            GameApiProbe.BeginSession();

            // 1) 设置面板：构造 → 加载/默认 → 挂到 Options → 注册本地化
            Settings = new RailCapacityGuardSetting(this);
            AssetDatabase.global.LoadSettings(
                nameof(RailCapacityGuard),
                Settings,
                new RailCapacityGuardSetting(this));
            Settings.RegisterInOptionsUI();
            RegisterTextLocales(Settings);
            // 让已保存的开关值立刻生效（重新构造的默认值实例不会触发属性 setter）
            ModLog.VerboseEnabled = Settings.EnableDiagnosticLogging;
            ModLog.Info("[Mod] settings registered: diagnosticLogging=" + Settings.EnableDiagnosticLogging
                + ", timetableDispatch=" + Settings.EnableTimetableDispatch
                + ", capacityFeedback=" + Settings.EnableCapacityFeedback);

            // 2) Service 容器（本步骤不注册任何业务 Service）
            Services = new ServiceRegistry();
            Services.Initialize(World.DefaultGameObjectInjectionWorld);
            if (!Services.IsInitialized)
            {
                // World 尚未就绪：SaveLoadHooksSystem.OnCreate 会兜底初始化。
                ModLog.Warn("[Mod] World.DefaultGameObjectInjectionWorld is null; deferred to SaveLoadHooksSystem.OnCreate");
            }

            // 3) IPre/PostDeserialize 必须显式注册，否则永不运行
            updateSystem.UpdateBefore<PreDeserialize<SaveLoadHooksSystem>>(SystemUpdatePhase.Deserialize);
            updateSystem.UpdateAfter<PostDeserialize<SaveLoadHooksSystem>>(SystemUpdatePhase.Deserialize);

            // 3b) P7 内核 + 时间基 + P8 低频诊断（必须用 UpdateAt 注册才会被调度）
            updateSystem.UpdateAt<RailTimebaseSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<TimetableDispatchSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<LineDiagnosticsSystem>(SystemUpdatePhase.GameSimulation);

            // 3b-2) 列车 tooltip：必须在 UITooltip 相位（dump:83182 = 23）才会被 tooltip 管线收集
            updateSystem.UpdateAt<TrainTooltipSystem>(SystemUpdatePhase.UITooltip);

            // 3c) 实验采集驱动——**已收回（2026-09-27，采集期结束）**：注销出调度队列；
            // 代码与 flag 门控（exp_start.flag）保留，重启实验时取消下行注释即可。
            // updateSystem.UpdateAt<ExperimentSamplerSystem>(SystemUpdatePhase.GameSimulation);

            // 4) 版本守卫汇总（唯一的缺失报警出口）
            GameApiProbe.ReportMissing();

            // 本步骤刻意不注册任何业务 ECS System（P1..P8 阶段逐个加）。
            ModLog.Info("[Mod] skeleton loaded; services=" + Services.Count + ", initialized=" + Services.IsInitialized);
        }

        /// <summary>
        /// 注册设置面板文案。
        ///
        /// 修复记录（汉化缺失）：旧实现只尝试 "en-US" 与 "zh-Hans"，且**不支持就跳过**，
        /// 实测日志出现 "[Mod] locale not supported by game, skipped: zh-Hans" → 中文从未注册，
        /// 中文界面因此回落成英文/键名。现在改为：
        ///   1. 英文文案注册到 en-US 与 fallbackLocaleId（保证任何语言下都不显示裸键名）
        ///   2. 中文文案注册到**所有** zh* 受支持语言（zh-Hans / zh-Hant / zh-CN / zh-TW …）
        ///   3. 当前语言是中文但游戏未支持时，用 AddLocale 主动创建该语言（参考上一版可运行 Mod 的做法）
        /// 依据（均实测于 Colossal.Localization.LocalizationManager）：
        ///   GetSupportedLocales():string[] / activeLocaleId / fallbackLocaleId /
        ///   SupportsLocale(string) / AddLocale(string, SystemLanguage, string) /
        ///   LocaleIdToSystemLanguage(string) / GetLocalizedName(string) / AddSource(string, IDictionarySource)
        /// </summary>
        private static void RegisterTextLocales(RailCapacityGuardSetting setting)
        {
            var localizationManager = GameManager.instance.localizationManager;
            if (localizationManager == null)
            {
                ModLog.Warn("[Mod] localizationManager is null; settings labels will fall back to locale keys");
                return;
            }

            string[] supported = null;
            try
            {
                supported = localizationManager.GetSupportedLocales();
            }
            catch (Exception ex)
            {
                ModLog.Warn("[Mod] GetSupportedLocales failed: " + ex.GetType().Name);
            }

            ModLog.Info("[Mod] i18n probe: active=" + localizationManager.activeLocaleId
                + " fallback=" + localizationManager.fallbackLocaleId
                + " supported=" + (supported == null ? "<null>" : string.Join(",", supported)));

            System.Collections.Generic.Dictionary<string, string> english = RailGuardLocales.BuildEnUs(setting);
            System.Collections.Generic.Dictionary<string, string> chinese = RailGuardLocales.BuildZhHans(setting);
            System.Collections.Generic.HashSet<string> done = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) 英文：en-US + fallback
            RegisterLocale(localizationManager, RailGuardLocales.EnUs, english, done);
            RegisterLocale(localizationManager, localizationManager.fallbackLocaleId, english, done);

            // 2) 中文：所有 zh* 受支持语言
            int chineseCount = 0;
            if (supported != null)
            {
                for (int i = 0; i < supported.Length; i++)
                {
                    string localeId = supported[i];
                    if (localeId != null && localeId.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                    {
                        if (RegisterLocale(localizationManager, localeId, chinese, done))
                        {
                            chineseCount++;
                        }
                    }
                }
            }

            // 3) 当前语言是中文但不在受支持列表 → 主动创建
            string active = localizationManager.activeLocaleId;
            if (chineseCount == 0 && !string.IsNullOrEmpty(active) && active.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                RegisterLocale(localizationManager, active, chinese, done);
            }

            if (chineseCount == 0)
            {
                ModLog.Warn("[Mod] 未在受支持语言中找到 zh* 条目；active=" + active
                    + "（若游戏语言确为中文，请把本行 i18n probe 发回）");
            }
        }

        private static bool RegisterLocale(
            Colossal.Localization.LocalizationManager localizationManager,
            string localeId,
            System.Collections.Generic.Dictionary<string, string> entries,
            System.Collections.Generic.HashSet<string> done)
        {
            if (string.IsNullOrEmpty(localeId) || !done.Add(localeId))
            {
                return false;
            }

            if (!localizationManager.SupportsLocale(localeId))
            {
                // 旧实现此处直接 return（中文因此丢失）；改为主动创建语言
                try
                {
                    localizationManager.AddLocale(
                        localeId,
                        localizationManager.LocaleIdToSystemLanguage(localeId),
                        localizationManager.GetLocalizedName(localeId));
                    ModLog.Info("[Mod] locale created: " + localeId);
                }
                catch (Exception ex)
                {
                    ModLog.Warn("[Mod] AddLocale failed for " + localeId + ": " + ex.GetType().Name);
                    return false;
                }
            }

            localizationManager.AddSource(localeId, new RailGuardLocaleSource(entries));
            ModLog.Info("[Mod] locale registered: " + localeId + " entries=" + entries.Count);
            return true;
        }

        public void OnDispose()
        {
            ModLog.Info(nameof(Mod) + "." + nameof(OnDispose));

            if (Services != null)
            {
                Services.Dispose();
                Services = null;
            }

            GameApiProbe.EndSession();
            ModLog.ResetState();
        }
    }
}
