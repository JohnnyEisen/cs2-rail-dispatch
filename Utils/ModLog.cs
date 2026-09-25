using Colossal.Logging;

namespace RailCapacityGuard.Utils
{
    /// <summary>
    /// 项目统一日志入口。日志必须有开关（开发规范第 8 条）：
    /// 默认只输出 Info/Warn/Error，Verbose 需显式打开。
    /// </summary>
    public static class ModLog
    {
        private static ILog s_Log;

        /// <summary>详细日志开关，默认关闭。</summary>
        public static bool VerboseEnabled { get; set; }

        public static bool IsInitialized => s_Log != null;

        public static void Initialize()
        {
            if (s_Log != null)
            {
                return;
            }

            s_Log = LogManager.GetLogger("RailCapacityGuard").SetShowsErrorsInUI(false);
        }

        public static void Info(string message)
        {
            if (s_Log != null)
            {
                s_Log.Info(message);
            }
        }

        public static void Warn(string message)
        {
            if (s_Log != null)
            {
                s_Log.Warn(message);
            }
        }

        public static void Error(string message)
        {
            if (s_Log != null)
            {
                s_Log.Error(message);
            }
        }

        public static void Verbose(string message)
        {
            if (VerboseEnabled && s_Log != null)
            {
                s_Log.Info(message);
            }
        }

        public static void ResetState()
        {
            VerboseEnabled = false;
        }
    }
}
