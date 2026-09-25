using Game.Net;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RailCapacityGuard.Services
{
    /// <summary>D1 列车长度采样（P0-B D1）。</summary>
    public struct TrainLengthSample
    {
        public float Meters;
        public Confidence Confidence;
        public EstimateSource Source;
    }

    /// <summary>D8 车站快照（P0-B D8）。字段均来自 Game.Buildings.TransportStation。</summary>
    public struct StationSnapshot
    {
        public Entity Building;
        public float ComfortFactor;
        public float LoadingFactor;
        public bool TransportStopsActive;
        public EnergyTypes TrainRefuelTypes;
    }

    /// <summary>D9 线路快照（P0-B D9）。字段来自 Game.Routes.TransportLine。</summary>
    public struct LineSnapshot
    {
        public Entity Route;
        public float VehicleInterval;
        public float UnbunchingFactor;
        public int TicketPrice;
        public bool RequireVehicles;
        public bool NotEnoughVehicles;
    }

    /// <summary>D9 线路段路径目标（Game.Routes.PathTargets）。</summary>
    public struct PathTargetsSnapshot
    {
        public Entity StartLane;
        public Entity EndLane;
        public float2 CurvePositions;
        public float3 ReadyStartPosition;
        public float3 ReadyEndPosition;
    }

    /// <summary>W2 信号快照（Game.Net.LaneSignal）。</summary>
    public struct SignalSnapshot
    {
        public LaneSignalType Signal;
        public LaneSignalFlags Flags;
        public sbyte Priority;
        public sbyte Default;
        public ushort GroupMask;
        public Entity Petitioner;
        public Entity Blocker;
    }

    /// <summary>W3 发车间隔快照（Game.Routes.TransportLine）。</summary>
    public struct HeadwaySnapshot
    {
        public float VehicleInterval;
        public float UnbunchingFactor;
        public bool RequireVehicles;
        public bool NotEnoughVehicles;
    }

    /// <summary>W3 发车间隔建议。只输出建议，写入必须由玩家确认后执行。</summary>
    public struct HeadwayAdvice
    {
        public float SuggestedInterval;
        public bool IsWithinVanillaBounds;
        public Confidence Confidence;
        public string Reason;
    }

    /// <summary>prefab 几何包围盒（Game.Prefabs.ObjectGeometryData）。</summary>
    public struct BoundsSnapshot
    {
        public float3 Min;
        public float3 Max;
    }

    /// <summary>D12 运行时包络（自算，原版实例几何不可读）。</summary>
    public struct EnvelopeSnapshot
    {
        public float Length;
        public float HalfWidth;
        public float Height;
        public Confidence Confidence;
    }

    /// <summary>D12 站台匹配结果。</summary>
    public struct FitResult
    {
        public bool Fits;
        public float TrainLength;
        public float PlatformUsable;
        public UnavailableReason Reason;
    }

    /// <summary>D13 车站度量（从原版推导，不进存档）。</summary>
    public struct StationMetrics
    {
        public int StationTrackCount;
        public float TotalTrackLength;
        public float LoadingFactor;
        public float ComfortFactor;
    }
}
