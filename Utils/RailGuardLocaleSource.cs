using System.Collections.Generic;
using Colossal;

namespace RailCapacityGuard.Utils
{
    /// <summary>
    /// 最小内存本地化源（en-US / zh-HANS / zh-HANT 各一份）。
    /// key 全部由 RailCapacityGuardSetting 实例的 locale key 生成器产出，不硬编码。
    /// </summary>
    internal sealed class RailGuardLocaleSource : IDictionarySource
    {
        private readonly Dictionary<string, string> m_Entries;

        public RailGuardLocaleSource(Dictionary<string, string> entries)
        {
            m_Entries = entries ?? new Dictionary<string, string>();
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return m_Entries;
        }

        public void Unload()
        {
        }
    }

    internal static class RailGuardLocales
    {
        public const string EnUs = "en-US";
        public const string ZhHans = "zh-HANS";
        public const string ZhHant = "zh-HANT";

        public static Dictionary<string, string> BuildEnUs(RailCapacityGuardSetting s)
        {
            Dictionary<string, string> m = Base(s, "Rail Capacity Guard", "General", "Timetable");

            Add(m, s, nameof(s.EnableDiagnosticLogging), "Diagnostic logging", "Write verbose P7/P8 diagnostics to the mod log.");
            Add(m, s, nameof(s.EnableTimetableDispatch), "Timetable dispatch", "Master switch: hold departures to the interval and let capacity feedback decide.");
            Add(m, s, nameof(s.EnableCapacityFeedback), "Capacity feedback", "Ask the target platform (LaneReservation) whether the train will fit on arrival.");
            Add(m, s, nameof(s.EnableThroatCoordination), "Throat coordination", "Hold a train when another line occupies the shared throat zone (experimental).");
            Add(m, s, nameof(s.EnableEarlyDeparture), "Early departure", "Allow leaving up to the early limit when a following train needs the platform.");
            Add(m, s, nameof(s.EnableFleetAdaptation), "Fleet adaptation", "Let the mod nudge the line's vehicle count through the vanilla policy (experimental).");
            Add(m, s, nameof(s.EnableClockMeasurement), "Measure day length", "Measure frames per in-game day at runtime (only needed with slow-clock mods).");
            Add(m, s, nameof(s.EnableTooltip), "Vehicle tooltip", "Hover a train to see its planned departure, headway and hold state.");
            Add(m, s, nameof(s.PostponeStepFrames), "Postpone step (frames)", "How far to push the departure when the platform is busy.");
            Add(m, s, nameof(s.SafetyMarginFrames), "Safety margin (frames)", "Extra slack required between arrival ETA and the occupant's departure.");
            Add(m, s, nameof(s.DiagnosticIntervalFrames), "Diagnostic interval (frames)", "How often the low-frequency P8 diagnostics run.");
            Add(m, s, nameof(s.MaxHoldMinutes), "Extra hold cap (0 = auto)", "Absolute cap on how long a train may be held; 0 = auto (20 game minutes).");
            Add(m, s, nameof(s.MinHeadwayMinutes), "Min departure spacing (min)", "Conflict check: minimum spacing kept between two written departures.");
            Add(m, s, nameof(s.MaxEarlyPercent), "Max early (% of run time)", "Upper bound on how early a train may leave, as a share of the segment run time.");
            Add(m, s, nameof(s.MaxBoardingMinutes), "Max station dwell (min)", "If boarding stalls (stuck passenger/pet), force departure after this many game minutes.");
            return m;
        }

        public static Dictionary<string, string> BuildZhHans(RailCapacityGuardSetting s)
        {
            Dictionary<string, string> m = Base(s, "铁路容量守卫", "常规", "发车时刻");

            Add(m, s, nameof(s.EnableDiagnosticLogging), "诊断日志", "把 P7/P8 的详细诊断写入 Mod 日志。");
            Add(m, s, nameof(s.EnableTimetableDispatch), "时刻表调度", "总开关：按间隔按住发车，并由容量反馈决定是否执行。");
            Add(m, s, nameof(s.EnableCapacityFeedback), "容量反馈", "询问目标站台（LaneReservation）：列车到站时是否进得去。");
            Add(m, s, nameof(s.EnableThroatCoordination), "咽喉区协调", "咽喉区被别的线路占用时按住本车（实验性）。");
            Add(m, s, nameof(s.EnableEarlyDeparture), "提前发车", "后方有车需要本站台时，允许在提前上限内提前发车。");
            Add(m, s, nameof(s.EnableFleetAdaptation), "车队自适应", "通过原版策略微调线路车辆数（实验性）。");
            Add(m, s, nameof(s.EnableClockMeasurement), "运行时测量日长", "运行时测量一游戏日的帧数（仅在装了慢时钟 Mod 时需要）。");
            Add(m, s, nameof(s.EnableTooltip), "列车提示", "鼠标悬浮列车时显示计划发车时间、发车间隔与按住状态。");
            Add(m, s, nameof(s.PostponeStepFrames), "推迟步长（帧）", "站台被占时，把发车帧往后推多少。");
            Add(m, s, nameof(s.SafetyMarginFrames), "安全余量（帧）", "到站 ETA 与占用者离开时间之间额外保留的余量。");
            Add(m, s, nameof(s.DiagnosticIntervalFrames), "诊断周期（帧）", "P8 低频诊断的运行间隔。");
            Add(m, s, nameof(s.MaxHoldMinutes), "附加等待上限（0=自动）", "最多按住多久；0 = 自动（20 游戏分钟）。");
            Add(m, s, nameof(s.MinHeadwayMinutes), "最小发车间距（分钟）", "冲突检查：两次写入发车帧之间至少保留的间距。");
            Add(m, s, nameof(s.MaxEarlyPercent), "最大提前比例（%）", "最多能提前多少发车（按站间运行时间的比例）。");
            Add(m, s, nameof(s.MaxBoardingMinutes), "停站硬上限（分钟）", "上下客卡死（卡住的乘客/宠物）时，超过该游戏分钟数即强制发车。");
            return m;
        }

        private static Dictionary<string, string> Base(
            RailCapacityGuardSetting setting,
            string title,
            string generalGroup,
            string timetableGroup)
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            map[setting.GetSettingsLocaleID()] = title;
            map[setting.GetOptionTabLocaleID(RailCapacityGuardSetting.kMainTab)] = title;
            map[setting.GetOptionGroupLocaleID(RailCapacityGuardSetting.kGeneralGroup)] = generalGroup;
            map[setting.GetOptionGroupLocaleID(RailCapacityGuardSetting.kTimetableGroup)] = timetableGroup;
            return map;
        }

        private static void Add(
            Dictionary<string, string> map,
            RailCapacityGuardSetting setting,
            string optionName,
            string label,
            string description)
        {
            map[setting.GetOptionLabelLocaleID(optionName)] = label;
            map[setting.GetOptionDescLocaleID(optionName)] = description;
        }
    }
}
