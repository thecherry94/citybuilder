using System.Numerics;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Tools;

/// <summary>How a proposed curve endpoint attaches to the existing network.</summary>
public abstract record EndpointBinding
{
    /// <summary>Endpoint stands free: a new node is created (or an existing node
    /// within reuse distance is picked up).</summary>
    public sealed record Free : EndpointBinding;

    /// <summary>Endpoint connects to an existing node.</summary>
    public sealed record AtNode(NodeId Node) : EndpointBinding;

    /// <summary>Endpoint lands on an existing edge, which is split at T first.</summary>
    public sealed record OnEdge(EdgeId Edge, float T) : EndpointBinding;

    public static readonly Free None = new();
}

public sealed record ProposedCurve(Bezier3 Curve, EndpointBinding Start, EndpointBinding End);

public sealed record PlacementProposal(IReadOnlyList<ProposedCurve> Curves, RoadTypeId Type);

public enum PlacementError { TooShort, SelfIntersecting, Overlapping, CrossingTooShallow }

/// <summary>Result of dry-running a proposal against the network. Only a valid,
/// current ValidatedPlacement can be committed.</summary>
public sealed record ValidatedPlacement(
    PlacementProposal Proposal,
    bool IsValid,
    IReadOnlyList<PlacementError> Errors,
    IReadOnlyList<Vector3> CrossingPoints,
    int NetworkVersion);

public sealed record CommitResult(
    bool Success,
    IReadOnlyList<EdgeId> CreatedEdges,
    IReadOnlyList<NodeId> CreatedNodes,
    string? FailureReason)
{
    public static CommitResult Failed(string reason)
        => new(false, Array.Empty<EdgeId>(), Array.Empty<NodeId>(), reason);
}
