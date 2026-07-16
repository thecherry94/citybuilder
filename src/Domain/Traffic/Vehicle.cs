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

    /// <summary>Per-driver personality scalar in [0,1], drawn once from the sim's
    /// seeded RNG at spawn time (TM:PE-style): higher means a more assertive driver —
    /// faster desired cruise speed and a smaller accepted gap at junctions. Defaults
    /// to 0.5 (neutral) so hand-constructed vehicles in tests behave exactly as
    /// before this feature existed.</summary>
    public float Profile { get; set; } = 0.5f;

    // --------------------------------------------------------- trip stats
    public float SpawnTime { get; set; }
    /// <summary>Free-flow time of the distance actually driven so far: accumulated as
    /// lane runs / connectors complete (length over limit), exact across replans.</summary>
    public float FreeFlowTime { get; set; }
    public int Stops { get; set; }            // 0.5 m/s downward crossings after having first moved
    public bool HasMoved { get; set; }

    /// <summary>Connector picked for the next junction (valid while on a lane).</summary>
    public (NodeId Node, int Connector)? PlannedConnector { get; set; }

    /// <summary>Segment the vehicle just left, and its length — the rear half of the
    /// car still renders on it until S exceeds half a car length.</summary>
    public LaneId? PrevLane { get; set; }
    public (NodeId Node, int Connector)? PrevCrossing { get; set; }
    public float PrevLength { get; set; }

    public float StuckTime { get; set; }
    public bool HasStopped { get; set; }          // stop-sign compliance latch
    public float WaitArrivalOrder { get; set; }   // all-way stop FIFO ticket
    public float JunctionWait { get; set; }       // seconds blocked at the line (impatience)
    public bool BlockedAtLine { get; set; }       // set by the arbiter wall each tick

    // dynamic lane change: while changing, the vehicle also occupies ChangeFrom
    public LaneId? ChangeFrom { get; set; }
    public float ChangeProgress { get; set; }     // 0..1
    public float ChangeCooldown { get; set; }

    public RouteStep CurrentStep => Route.Steps[StepIndex];
    public bool OnLastStep => StepIndex == Route.Steps.Count - 1;
}
