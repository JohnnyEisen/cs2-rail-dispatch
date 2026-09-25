using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using RailCapacityGuard.Utils;

namespace RailCapacityGuard.Services
{
    /// <summary>
    /// 版本守卫 + 反射适配层。
    ///
    /// 硬约束：反射调用只允许出现在本类（以及确需时的 VanillaCallAdapter）。
    /// 业务 Service 内不得出现 GetField / GetMethod / GetProperty。
    ///
    /// 用法：Mod.OnLoad 里 BeginSession() → 各 Service 的 Initialize() 里声明依赖
    ///       → ReportMissing() 汇总一条警告 → 游戏版本变化时只改这里的清单。
    /// 结果缓存在进程内字典，不进存档。
    /// </summary>
    public static class GameApiProbe
    {
        private const BindingFlags MemberFlags =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        private static readonly Dictionary<string, bool> s_Cache =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        private static readonly List<string> s_Missing = new List<string>();
        private static bool s_SessionActive;

        public static bool SessionActive => s_SessionActive;

        public static IReadOnlyList<string> Missing => s_Missing;

        public static void BeginSession()
        {
            s_Cache.Clear();
            s_Missing.Clear();
            s_SessionActive = true;
        }

        public static void EndSession()
        {
            s_Missing.Clear();
            s_Cache.Clear();
            s_SessionActive = false;
        }

        /// <summary>字段 / 属性 / 方法任一存在即视为满足。</summary>
        public static bool Require(Type type, string memberName)
        {
            return RequireField(type, memberName)
                || RequireProperty(type, memberName)
                || RequireAnyMethod(type, memberName);
        }

        public static bool RequireField(Type type, string fieldName)
        {
            return Probe(type, "field:" + fieldName, t => t.GetField(fieldName, MemberFlags) != null);
        }

        public static bool RequireProperty(Type type, string propertyName)
        {
            return Probe(type, "prop:" + propertyName, t => t.GetProperty(propertyName, MemberFlags) != null);
        }

        /// <summary>parameterTypes 为空时只按名字匹配；否则要求参数类型逐个精确匹配。</summary>
        public static bool RequireMethod(Type type, string methodName, params Type[] parameterTypes)
        {
            string key = "method:" + methodName + "(" + parameterTypes.Length + ")";
            return Probe(type, key, t =>
            {
                MethodInfo[] methods = t.GetMethods(MemberFlags);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (!string.Equals(m.Name, methodName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (parameterTypes.Length == 0)
                    {
                        return true;
                    }

                    ParameterInfo[] parameters = m.GetParameters();
                    if (parameters.Length != parameterTypes.Length)
                    {
                        continue;
                    }

                    bool match = true;
                    for (int p = 0; p < parameters.Length; p++)
                    {
                        Type expected = parameterTypes[p];
                        Type actual = parameters[p].ParameterType;
                        if (actual.IsByRef && !expected.IsByRef)
                        {
                            actual = actual.GetElementType();
                        }
                        if (expected.IsByRef)
                        {
                            expected = expected.GetElementType();
                        }
                        if (actual != expected)
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        return true;
                    }
                }

                return false;
            });
        }

        private static bool RequireAnyMethod(Type type, string methodName)
        {
            return Probe(type, "anyMethod:" + methodName, t =>
            {
                MethodInfo[] methods = t.GetMethods(MemberFlags);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (string.Equals(methods[i].Name, methodName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            });
        }

        private static bool Probe(Type type, string key, Func<Type, bool> test)
        {
            if (type == null)
            {
                RecordMissing("<null type>." + key);
                return false;
            }

            string cacheKey = type.FullName + "|" + key;
            bool exists;
            if (s_Cache.TryGetValue(cacheKey, out exists))
            {
                return exists;
            }

            try
            {
                exists = test(type);
            }
            catch (Exception ex)
            {
                ModLog.Warn("[GameApiProbe] reflection failed on " + cacheKey + ": " + ex.GetType().Name);
                exists = false;
            }

            s_Cache[cacheKey] = exists;
            if (!exists)
            {
                RecordMissing(cacheKey);
            }

            return exists;
        }

        private static void RecordMissing(string cacheKey)
        {
            if (!s_Missing.Contains(cacheKey))
            {
                s_Missing.Add(cacheKey);
            }
        }

        /// <summary>汇总一条警告。游戏版本不匹配时这里是唯一的报警出口。</summary>
        public static void ReportMissing()
        {
            if (s_Missing.Count == 0)
            {
                ModLog.Info("[GameApiProbe] all required members resolved (probed=" + s_Cache.Count + ")");
                return;
            }

            StringBuilder builder = new StringBuilder();
            builder.Append("[GameApiProbe] MISSING ").Append(s_Missing.Count).Append(" member(s); ");
            builder.Append("dependent services are degraded to conservative-reject. ");

            int limit = Math.Min(s_Missing.Count, 20);
            for (int i = 0; i < limit; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(s_Missing[i]);
            }

            if (s_Missing.Count > limit)
            {
                builder.Append(", ...");
            }

            ModLog.Warn(builder.ToString());
        }
    }
}
