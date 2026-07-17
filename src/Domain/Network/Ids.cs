namespace CityBuilder.Domain.Network;

public readonly record struct NodeId(int Value);
public readonly record struct EdgeId(int Value);
public readonly record struct LaneId(int Value);
public readonly record struct RoadTypeId(int Value);
public readonly record struct RoundaboutId(int Value);

public enum LaneDirection { Forward, Backward }

public enum LaneKind { Driving, Bicycle, Sidewalk }
