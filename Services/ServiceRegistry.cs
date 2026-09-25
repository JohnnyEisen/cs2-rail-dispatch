using System;
using System.Collections.Generic;
using RailCapacityGuard.Utils;
using Unity.Entities;

namespace RailCapacityGuard.Services
{
    /// <summary>
    /// Service 容器。
    ///
    /// 依赖注入规则（P0-B 决策 S2）：
    ///   * 各 Service 之间的依赖一律通过构造函数参数传入；
    ///   * 只有"ECS System → Registry"这一处桥接使用静态 Current
    ///     （ECS System 由游戏创建，无法构造函数注入）。
    /// </summary>
    public sealed class ServiceRegistry
    {
        private readonly List<IRailGuardService> m_Services = new List<IRailGuardService>();
        private readonly Dictionary<Type, IRailGuardService> m_ByType = new Dictionary<Type, IRailGuardService>();

        /// <summary>唯一的静态桥接点，见类注释。</summary>
        public static ServiceRegistry Current { get; private set; }

        public World World { get; private set; }

        public bool IsInitialized { get; private set; }

        public int Count => m_Services.Count;

        public ServiceRegistry()
        {
            Current = this;
        }

        /// <summary>
        /// Mod.OnLoad 调用。World 可能尚不可用（为 null）时才不初始化，
        /// 此时由 SaveLoadHooksSystem.OnCreate 用系统自己的 World 兜底。
        /// </summary>
        public void Initialize(World world)
        {
            if (IsInitialized || world == null)
            {
                return;
            }

            World = world;
            IsInitialized = true;
            RegisterServices();
        }

        /// <summary>兜底初始化：已初始化则直接返回。</summary>
        public void EnsureInitialized(World world)
        {
            if (!IsInitialized)
            {
                Initialize(world);
            }
        }

        /// <summary>
        /// P1..P8 阶段在此注册各 Service（本步骤不注册任何业务 Service）。
        /// 注册时必须显式注入依赖，例如：
        ///   var segments = new TrackSegmentService(World);
        ///   Add(segments);
        ///   Add(new TrainLengthService(World, segments));
        /// </summary>
        private void RegisterServices()
        {
            // P2 / P3 / 解析 / 车队。依赖一律构造注入（P0-B 决策 S2）。
            Add(new PlatformCapacityService(World));
            Add(new ThroatZoneService(World));

            var resolver = new StationResolverService(World);
            Add(resolver);
            Add(new FleetPolicyService(World, resolver));

            ModLog.Info("[ServiceRegistry] RegisterServices done, services=" + m_Services.Count);
        }

        public void Add<T>(T service) where T : class, IRailGuardService
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            Type key = typeof(T);
            if (m_ByType.ContainsKey(key))
            {
                throw new InvalidOperationException("service already registered: " + key.FullName);
            }

            m_Services.Add(service);
            m_ByType.Add(key, service);
        }

        public T GetService<T>() where T : class, IRailGuardService
        {
            T service;
            if (!TryGetService(out service))
            {
                throw new InvalidOperationException("service not registered: " + typeof(T).FullName);
            }

            return service;
        }

        public bool TryGetService<T>(out T service) where T : class, IRailGuardService
        {
            IRailGuardService found;
            if (m_ByType.TryGetValue(typeof(T), out found))
            {
                service = (T)found;
                return true;
            }

            service = null;
            return false;
        }

        /// <summary>遍历所有 Service 清空缓存。读档前 / Mod 卸载时调用。</summary>
        public void ResetAll()
        {
            for (int i = 0; i < m_Services.Count; i++)
            {
                try
                {
                    m_Services[i].ResetState();
                }
                catch (Exception ex)
                {
                    ModLog.Error("[ServiceRegistry] ResetState failed for " + m_Services[i].Name + ": " + ex);
                }
            }

            ModLog.Verbose("[ServiceRegistry] ResetAll done, services=" + m_Services.Count);
        }

        /// <summary>Mod 卸载：清空列表并解除静态桥接。</summary>
        public void Dispose()
        {
            ResetAll();
            m_Services.Clear();
            m_ByType.Clear();
            World = null;
            IsInitialized = false;
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        }
    }
}
