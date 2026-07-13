using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Traffic;

/// <summary>A simulated car. Position is either on a lane (Lane set, Crossing null)
/// or on a junction connector (Crossing set). S is metres travelled along the current
/// lane's drawable span / connector curve, in travel direction.</summary>
public sealed class Vehicle
{
    public const float Length = 4.5f;

    public required int Id { get; init; }
    public required Route Route { get; set; }
    public int StepIndex { get; set; }

    public LaneId? Lane { get; set; }
    public (NodeId Node, int Connector)? Crossing { get; set; }
    public float S { get; set; }
    public float Speed { get; set; }
    public float Accel { get; set; }

    /// <summary>Connector picked for the next junction (valid while on a lane).</summary>
    public (NodeId Node, int Connector)? PlannedConnector { get; set; }

    public float StuckTime { get; set; }
    public bool HasStopped { get; set; }          // stop-sign compliance latch
    public float WaitArrivalOrder { get; set; }   // all-way stop FIFO ticket

    // dynamic lane change: while changing, the vehicle also occupies ChangeFrom
    public LaneId? ChangeFrom { get; set; }
    public float ChangeProgress { get; set; }     // 0..1
    public float ChangeCooldown { get; set; }

    public RouteStep CurrentStep => Route.Steps[StepIndex];
    public bool OnLastStep => StepIndex == Route.Steps.Count - 1;
}
