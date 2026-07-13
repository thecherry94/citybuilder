using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Traffic;

/// <summary>
/// Dynamic lane changes (MOBIL-lite). Discretionary: change when the projected IDM
/// acceleration gain beats a threshold and the new follower stays within comfortable
/// braking. Mandatory: within a window before a junction, pressure toward a lane whose
/// connectors serve the route's next movement, with urgency-relaxed safety. A change
/// takes 2 s during which the vehicle occupies both lanes.
/// </summary>
public sealed partial class TrafficSim
{
    private const float ChangeDuration = 2f;
    private const float EvalInterval = 0.5f;
    private const float PostChangeCooldown = 2.5f;
    private const float MandatoryWindow = 80f;
    private const float LockInZone = 25f;
    private const float DiscretionaryGain = 0.3f;
    private const float SafeFollowerDecel = 2.5f;
    private const float UrgentFollowerDecel = 4.0f;

    private void UpdateLaneChange(Vehicle v, float dt)
    {
        if (v.ChangeFrom is { } from)
        {
            v.ChangeProgress += dt / ChangeDuration;
            if (v.ChangeProgress >= 1f)
            {
                if (_laneVehicles.TryGetValue(from, out var fq))
                    fq.Remove(v);
                v.ChangeFrom = null;
                v.ChangeProgress = 0f;
                v.ChangeCooldown = PostChangeCooldown;
            }
            return;
        }

        v.ChangeCooldown -= dt;
        if (v.ChangeCooldown > 0 || v.Lane is null)
            return;
        v.ChangeCooldown = EvalInterval;
        MaybeChangeLane(v);
    }

    private void MaybeChangeLane(Vehicle v)
    {
        var laneId = v.Lane!.Value;
        if (!_adjacent.TryGetValue(laneId, out var adj) || (adj.Left is null && adj.Right is null))
            return;
        float distToCut = _runs[laneId].Length - v.S;
        if (distToCut < v.Speed * ChangeDuration + 6f)
        {
            // couldn't finish the manoeuvre before the junction at this speed;
            // mandatory merges retry once the wrong-lane wall has slowed us down
            bool serves0 = v.OnLastStep || v.PlannedConnector is not null;
            if (serves0 || v.Speed > 3f)
                return;
        }
        bool serves = v.OnLastStep || v.PlannedConnector is not null;

        if (!serves && distToCut < MandatoryWindow)
        {
            // urgency grows as the junction nears
            float urgency = 1f - distToCut / MandatoryWindow;
            float maxDecel = SafeFollowerDecel + (UrgentFollowerDecel - SafeFollowerDecel) * urgency;
            // prefer the neighbor that serves; otherwise drift left (turn lanes are
            // usually left of through lanes only for lefts — try both, serving first)
            foreach (var candidate in new[] { adj.Left, adj.Right }
                         .Where(c => c is not null)
                         .OrderByDescending(c => ServesNextMovement(c!.Value, v.Route, v.StepIndex)))
                if (TryChange(v, candidate!.Value, maxDecel, requireGain: false))
                    return;
            return;
        }

        if (distToCut < LockInZone)
            return; // committed to the junction approach

        foreach (var candidate in new[] { adj.Left, adj.Right })
        {
            if (candidate is null)
                continue;
            // never trade away the turn lane close to the junction
            if (distToCut < MandatoryWindow && !ServesNextMovement(candidate.Value, v.Route, v.StepIndex))
                continue;
            if (TryChange(v, candidate.Value, SafeFollowerDecel, requireGain: true))
                return;
        }
    }

    private bool TryChange(Vehicle v, LaneId target, float maxFollowerDecel, bool requireGain)
    {
        var targetQueue = _laneVehicles[target];
        float limit = _runs[target].SpeedLimit;

        Vehicle? leader = null, follower = null;
        foreach (var other in targetQueue)
        {
            if (other.S >= v.S)
                leader = other;            // queue is sorted desc: last ≥ wins? scan all
            else
            {
                follower = other;          // first below v.S (desc order → closest below)
                break;
            }
        }

        float gapLead = leader is null
            ? Idm.FreeGap
            : leader.S - Vehicle.Length - v.S;
        float gapFoll = follower is null
            ? Idm.FreeGap
            : v.S - Vehicle.Length - follower.S;
        if (gapLead < 1f || gapFoll < 0.5f)
            return false;

        float accNew = Idm.Accel(v.Speed, limit, gapLead,
            leader is null ? 0 : v.Speed - leader.Speed);
        if (requireGain)
        {
            var (curGap, curDv) = LeaderGap(v);
            float accCur = Idm.Accel(v.Speed, _runs[v.Lane!.Value].SpeedLimit, curGap, curDv);
            if (accNew - accCur < DiscretionaryGain)
                return false;
        }
        if (follower is not null)
        {
            float follAcc = Idm.Accel(follower.Speed, limit, gapFoll, follower.Speed - v.Speed);
            if (follAcc < -maxFollowerDecel)
                return false;
        }

        // commit: occupy both lanes until the change completes
        var from = v.Lane!.Value;
        v.ChangeFrom = from;
        v.ChangeProgress = 0f;
        v.Lane = target;
        v.PlannedConnector = PickConnector(v, target);
        targetQueue.Add(v);
        SortQueue(targetQueue);
        return true;
    }
}
