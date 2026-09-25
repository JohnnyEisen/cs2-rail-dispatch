using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using RailCapacityGuard.Utils;

namespace RailCapacityGuard
{
    /// <summary>
    /// Mod 设置面板（P7 完整版）。
    ///
    /// 拍板记录：① TrainLengthSafetyMargin 已删除（P1 废除）；② EnableCapacityFeedback 默认 true（V1 已确认站台轨道带 LaneReservation）；
    /// ③ 不用 AddComponentData；④ EnableThroatCoordination 默认 false；⑤ UI 视觉搁置。
    ///
    /// 依据（全部经反射表/实测确认）：ModSetting 基类；必须 override 的只有 SetDefaults()；
    /// 设置项是属性（bool → Toggle，float/int + [SettingsUISlider] → 滑块）。
    /// </summary>
    [FileLocation("RailCapacityGuard")]
    [SettingsUITabOrder(kMainTab)]
    [SettingsUIGroupOrder(kGeneralGroup, kTimetableGroup)]
    [SettingsUIShowGroupName(kGeneralGroup, kTimetableGroup)]
    public class RailCapacityGuardSetting : ModSetting
    {
        public const string kMainTab = "Main";
        public const string kGeneralGroup = "General";
        public const string kTimetableGroup = "Timetable";

        private bool m_EnableDiagnosticLogging;

        private bool m_EnableTimetableDispatch;
        private bool m_EnableCapacityFeedback;
        private bool m_EnableThroatCoordination;
        private bool m_EnableEarlyDeparture;
        private bool m_EnableFleetAdaptation;
        private bool m_EnableClockMeasurement;
        private bool m_EnableTooltip;

        private int m_PostponeStepFrames;
        private int m_SafetyMarginFrames;
        private int m_DiagnosticIntervalFrames;
        private float m_MaxHoldMinutes;
        private float m_MinHeadwayMinutes;
        private float m_MaxBoardingMinutes;
        private int m_MaxEarlyPercent;

        public RailCapacityGuardSetting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        public override void SetDefaults()
        {
            m_EnableDiagnosticLogging = false;

            m_EnableTimetableDispatch = true;
            m_EnableCapacityFeedback = true;
            m_EnableThroatCoordination = false;
            m_EnableEarlyDeparture = true;
            m_EnableFleetAdaptation = false;
            m_EnableClockMeasurement = false;
            m_EnableTooltip = true;

            m_PostponeStepFrames = 16;
            m_SafetyMarginFrames = 120;
            m_DiagnosticIntervalFrames = 4096;
            m_MaxHoldMinutes = 0f;   // 修复 3（方案 B）：0 = 自动 = 1 个图定间隔，仅作极端兜底
            m_MinHeadwayMinutes = 2f;    // 冲突检查的最小发车间距（与 interval 无关）
            m_MaxBoardingMinutes = 180f;  // 停站硬上限：3 游戏小时；超过则无视原版 Boarding 强制发车
            m_MaxEarlyPercent = 20;     // 提前发车上限 = 站间运行时间 × 该值
        }

        [SettingsUISection(kMainTab, kGeneralGroup)]
        public bool EnableDiagnosticLogging
        {
            get => m_EnableDiagnosticLogging;
            set
            {
                m_EnableDiagnosticLogging = value;
                ModLog.VerboseEnabled = value;
            }
        }

        [SettingsUISection(kMainTab, kTimetableGroup)]
        public bool EnableTimetableDispatch
        {
            get => m_EnableTimetableDispatch;
            set => m_EnableTimetableDispatch = value;
        }

        [SettingsUISection(kMainTab, kTimetableGroup)]
        public bool EnableCapacityFeedback
        {
            get => m_EnableCapacityFeedback;
            set => m_EnableCapacityFeedback = value;
        }

        [SettingsUISection(kMainTab, kTimetableGroup)]
        public bool EnableThroatCoordination
        {
            get => m_EnableThroatCoordination;
            set => m_EnableThroatCoordination = value;
        }

        [SettingsUISection(kMainTab, kTimetableGroup)]
        public bool EnableEarlyDeparture
        {
            get => m_EnableEarlyDeparture;
            set => m_EnableEarlyDeparture = value;
        }

        /// <summary>
        /// 修复 0：**硬性关闭** —— getter 恒返回 false，不读存档设置。
        /// 理由：存档里可能被手工开过，而车队写入依赖单位未证的 VehicleTiming.m_AverageTravelTime（V12），
        /// 已实测写出过 delta=-43.6/-89.3/-149.6（分钟）这类脏值。写入路径 ReadFleetPass 因此永远不会执行。
        /// </summary>
        [SettingsUISection(kMainTab, kTimetableGroup)]
        public bool EnableFleetAdaptation
        {
            get => false;
            set => m_EnableFleetAdaptation = value;
        }

        [SettingsUISection(kMainTab, kTimetableGroup)]
        public bool EnableClockMeasurement
        {
            get => m_EnableClockMeasurement;
            set => m_EnableClockMeasurement = value;
        }

        [SettingsUISection(kMainTab, kTimetableGroup)]
        public bool EnableTooltip
        {
            get => m_EnableTooltip;
            set => m_EnableTooltip = value;
        }

        [SettingsUISlider(min = 8f, max = 120f, step = 8f)]
        [SettingsUISection(kMainTab, kTimetableGroup)]
        public int PostponeStepFrames
        {
            get => m_PostponeStepFrames;
            set => m_PostponeStepFrames = value;
        }

        [SettingsUISlider(min = 0f, max = 600f, step = 30f)]
        [SettingsUISection(kMainTab, kTimetableGroup)]
        public int SafetyMarginFrames
        {
            get => m_SafetyMarginFrames;
            set => m_SafetyMarginFrames = value;
        }

        [SettingsUISlider(min = 1024f, max = 8192f, step = 1024f)]
        [SettingsUISection(kMainTab, kTimetableGroup)]
        public int DiagnosticIntervalFrames
        {
            get => m_DiagnosticIntervalFrames;
            set => m_DiagnosticIntervalFrames = value;
        }

        [SettingsUISlider(min = 0f, max = 10f, step = 0.5f)]
        [SettingsUISection(kMainTab, kTimetableGroup)]
        public float MaxHoldMinutes
        {
            get => m_MaxHoldMinutes;
            set => m_MaxHoldMinutes = value;
        }

        [SettingsUISlider(min = 0.5f, max = 30f, step = 0.5f)]
        [SettingsUISection(kMainTab, kTimetableGroup)]
        public float MinHeadwayMinutes
        {
            get => m_MinHeadwayMinutes;
            set => m_MinHeadwayMinutes = value;
        }

        [SettingsUISlider(min = 0f, max = 40f, step = 5f)]
        [SettingsUISection(kMainTab, kTimetableGroup)]
        public int MaxEarlyPercent
        {
            get => m_MaxEarlyPercent;
            set => m_MaxEarlyPercent = value;
        }

        /// <summary>
        /// 停站硬上限（游戏分钟，默认 180 = 3 游戏小时）。
        /// 用途：原版 boarding 可能因卡住的乘客/宠物无限等待 → 超过该上限即无视 Boarding 位强制发车。
        /// 只在 Boarding 进行中生效，正常上下客完全不受影响。
        /// </summary>
        [SettingsUISlider(min = 10f, max = 600f, step = 10f)]
        [SettingsUISection(kMainTab, kTimetableGroup)]
        public float MaxBoardingMinutes
        {
            get => m_MaxBoardingMinutes;
            set => m_MaxBoardingMinutes = value;
        }
    }
}
