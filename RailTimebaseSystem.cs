using Game;
using Game.Simulation;
using RailCapacityGuard.Utils;
using Unity.Entities;

namespace RailCapacityGuard
{
    /// <summary>
    /// 时间基（自写；常量与测量思路参考 Transit Timetables/TimebaseSystem.cs，MIT）。
    ///
    /// 已核实：1 游戏日 = 262144 帧 @1x，1 帧 = 1 tick（@@TimeSystem.kTicksPerDay=262144@@ 同值）。
    /// 默认直接使用原版常量（bit-identical）；只有当玩家开启"运行时测量"（给慢时钟 Mod 用）时
    /// 才用 normalizedTime 累计推算一日帧数，并做合理区间校验。
    /// </summary>
    public partial class RailTimebaseSystem : GameSystemBase
    {
        public const double VanillaTicksPerDay = 262144.0;

        private const double DMin = 65536.0;      // 0.25x
        private const double DMax = 4194304.0;    // 16x
        private const uint MinFrames = 2048u;     // 累计样本门槛
        private const double MinClockDelta = 2e-4;

        private SimulationSystem m_Sim;
        private TimeSystem m_Time;

        private double m_TicksPerDay = VanillaTicksPerDay;
        private uint m_AnchorFrame;
        private float m_AnchorClock;
        private bool m_HaveAnchor;

        public double TicksPerDay => m_TicksPerDay;
        public float FramesPerMinute => (float)(m_TicksPerDay / 1440.0);
        public float UnitMinutes => (float)(86400.0 / m_TicksPerDay);
        public bool Measured { get; private set; }

        public uint CurrentFrame => m_Sim != null ? m_Sim.frameIndex : 0u;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Time = World.GetOrCreateSystemManaged<TimeSystem>();
        }

        protected override void OnUpdate()
        {
            var settings = Mod.Settings;
            if (settings == null || !settings.EnableClockMeasurement)
            {
                m_TicksPerDay = VanillaTicksPerDay;
                m_HaveAnchor = false;
                Measured = false;
                return;
            }

            if (m_Sim == null || m_Time == null)
            {
                return;
            }

            uint frame = m_Sim.frameIndex;
            float clock = m_Time.normalizedTime;

            if (!m_HaveAnchor)
            {
                m_AnchorFrame = frame;
                m_AnchorClock = clock;
                m_HaveAnchor = true;
                return;
            }

            uint elapsed = frame - m_AnchorFrame;
            float deltaClock = clock - m_AnchorClock;
            if (deltaClock < 0f)
            {
                deltaClock += 1f; // 跨日回绕
            }

            if (elapsed < MinFrames || deltaClock < MinClockDelta)
            {
                return;
            }

            double measured = elapsed / (double)deltaClock;
            if (measured >= DMin && measured <= DMax)
            {
                m_TicksPerDay = measured;
                Measured = true;
                ModLog.Verbose("[Timebase] measured ticksPerDay=" + measured.ToString("F0")
                    + " fpm=" + FramesPerMinute.ToString("F2"));
            }

            m_AnchorFrame = frame;
            m_AnchorClock = clock;
        }
    }
}
