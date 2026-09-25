using Game;
using Unity.Collections;
using Unity.Entities;
using RailCapacityGuard.Services;
using RailCapacityGuard.Utils;

namespace RailCapacityGuard
{
    /// <summary>
    /// P8 低频诊断（每 DiagnosticIntervalFrames，默认 4096 帧）。**只读**：不写任何组件、不干预发车。
    /// 输出（Verbose 日志）：线路车辆数、总载客容量（V3 字段）、站台轨道数、当前间隔、实测圈时。
    /// 区间容量按其定位降级到这里（不走进发车决策）。
    /// </summary>
    public partial class LineDiagnosticsSystem : GameSystemBase
    {
        private const int MaxLines = 32;
        private const int MaxVehiclesPerLine = 32;

        private RailTimebaseSystem m_Timebase;
        private EntityQuery m_LineQuery;
        private StationResolverService m_Resolver;
        private ThroatZoneService m_Throat;
        private uint m_LastRunFrame;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 512;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Timebase = World.GetOrCreateSystemManaged<RailTimebaseSystem>();
            m_LineQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Routes.Route>(),
                    ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                    ComponentType.ReadOnly<Game.Routes.RouteWaypoint>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Game.Common.Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                },
            });
        }

        protected override void OnUpdate()
        {
            var settings = Mod.Settings;
            if (settings == null)
            {
                return;
            }

            var registry = ServiceRegistry.Current;
            if (registry == null || !registry.IsInitialized)
            {
                return;
            }

            if (m_Resolver == null)
            {
                if (!registry.TryGetService(out m_Resolver) || !registry.TryGetService(out m_Throat))
                {
                    return;
                }
            }

            uint now = m_Timebase.CurrentFrame;
            uint interval = (uint)System.Math.Max(1024, settings.DiagnosticIntervalFrames);
            if (m_LastRunFrame != 0u && now - m_LastRunFrame < interval)
            {
                return;
            }

            m_LastRunFrame = now;

            EntityManager em = EntityManager;
            float fpm = m_Timebase.FramesPerMinute;

            NativeArray<Entity> lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                int limit = System.Math.Min(lines.Length, MaxLines);
                for (int i = 0; i < limit; i++)
                {
                    Entity line = lines[i];
                    if (!em.HasBuffer<Game.Routes.RouteVehicle>(line))
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Routes.RouteVehicle> vehicles = em.GetBuffer<Game.Routes.RouteVehicle>(line, true);
                    int vehicleCount = System.Math.Min(vehicles.Length, MaxVehiclesPerLine);
                    int totalCapacity = 0;
                    for (int v = 0; v < vehicleCount; v++)
                    {
                        Entity vehicle = vehicles[v].m_Vehicle;
                        if (!em.Exists(vehicle) || !em.HasComponent<Game.Prefabs.PrefabRef>(vehicle))
                        {
                            continue;
                        }

                        Entity prefab = em.GetComponentData<Game.Prefabs.PrefabRef>(vehicle).m_Prefab;
                        if (prefab != Entity.Null && em.Exists(prefab)
                            && em.HasComponent<Game.Prefabs.PublicTransportVehicleData>(prefab))
                        {
                            totalCapacity += em.GetComponentData<Game.Prefabs.PublicTransportVehicleData>(prefab).m_PassengerCapacity;
                        }
                    }

                    Game.Routes.TransportLine lineData = em.GetComponentData<Game.Routes.TransportLine>(line);
                    int platformLanes = m_Resolver.CountPlatformLanes(em, line);

                    ModLog.Verbose("[P8] line=" + line.Index
                        + " vehicles=" + vehicleCount
                        + " totalCapacity=" + totalCapacity
                        + " platformLanes=" + platformLanes
                        + " vanillaIntervalUnits=" + lineData.m_VehicleInterval.ToString("F2")   // 仅诊断：新设计不再使用该字段
                        + " fpm=" + fpm.ToString("F1")
                        + " zones=" + m_Throat.ZoneCount);
                }
            }
            finally
            {
                lines.Dispose();
            }
        }
    }
}
