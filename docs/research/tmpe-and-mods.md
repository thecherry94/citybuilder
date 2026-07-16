# TM:PE and adjacent mods: traffic-AI research for tuning our sim

Research date: 2026-07-16. Sources: `github.com/CitiesSkylinesMods/TMPE` (master branch,
commit reachable via `git clone` at research time), the TMPE GitHub wiki (cloned from
`TMPE.wiki.git`), `github.com/MacSergey/AdvancedVehicleOptions`, and
`github.com/krzychu124/Traffic` (the CS2 successor to TM:PE). Old
`tmpe.viathinksoft.com` wiki pages are dead (SNI/403 errors) — where the current wiki
redirects there, that specific page's content could not be recovered; noted inline.

All file paths below are `TLM/TLM/...` relative to the TMPE repo root unless stated
otherwise. Line numbers refer to the `master` branch at fetch time and may drift.

---

## 1. Vanilla CS1 behavior, as TM:PE's own docs describe it

TM:PE's wiki is unusually explicit about what vanilla Cities: Skylines *doesn't* do —
this is effectively a bug list for the base game's traffic AI:

- **No driver personality at all.** "In the vanilla game, the only difference between
  drivers is the vehicle asset they drive... it's effects on traffic are still minimal."
  Every car of the same asset drives identically. (`Individual-Driving-Styles.md`)
- **Lane choice is decided once, at path-start, and never revisited.** The wiki
  motivates Dynamic Lane Selection as reducing "single lane bunching... along long
  stretches of road" — implying vanilla vehicles pick a lane at spawn/pathfind time and
  stick with it regardless of how traffic evolves around them, causing one lane to
  clump while others run empty. (`Individual-Driving-Styles.md`, `Dynamic-Lane-Selection.md`)
- **No real car-following / position reservation is inherently loose.** TM:PE's own
  fix (`AltLaneSelectionMaxReservedSpace`, see §3) exists because vanilla lane-space
  reservation isn't tight enough to prevent unrealistic gap behavior when changing
  lanes; the reservation check is a retrofit, not a built-in car-following model.
- **Priority at junctions is a fixed right-hand/left-hand rule only**, with no
  volume-awareness: "Vehicles drive on Left? Traffic approaching from the right has
  priority... these basic traffic rules aren't always sufficient" — vanilla can't
  express "Main St should beat Side St" without TM:PE's explicit priority signs.
  (`Priority-Signs.md`)
- **Speed limits are enforced as a hard per-segment/lane cap** baked into the network
  prefab (`NetInfo.Lane.m_speedLimit`); TM:PE's speed-limit tool just lets you author
  different values into the same slot the vanilla game already reads (`GetGameSpeedLimit`,
  `SpeedLimitManager.cs:256-278`). This means **vanilla already has curve/turn slowdown
  and acceleration physics built into the base `CarAI`/vehicle prefab** — TM:PE
  documents that it does not reimplement this (see §2, "TM:PE does not touch
  acceleration").
- **Vehicle capacities matter more than expected.** Oversized vehicle capacities (a
  hearse holding 100 corpses instead of 10) break the AI's dispatch logic — "AI gets
  stuck on the boarding phase." Speculative extrapolation to our sim: any building/vehicle
  capacity mismatch is a broader class of bug, not traffic-specific. (`Vanilla-capacities.md`)
- **Junctions with only 2 segments are usually not "real" junctions** — TM:PE
  distinguishes terminal, middle/segment, bend, 2-segment, and asymmetric nodes, each
  with different default lane-change/U-turn/crossing rules
  (`Nodes,-Segments,-Lanes.md`). Bend nodes in particular are flagged "AVOID!! ...
  likely to break the vehicle AIs" for tracked vehicles — a fragility class worth
  keeping in mind if our sim ever supports tracks.

---

## 2. TM:PE mechanisms

### 2.1 Individual Driving Styles (IDS) — the "personality" system

Source of truth: `ExtVehicleManager.cs` (`UpdateDynamicLaneSelectionParameters`,
`GetTimedVehicleRand`, `StepRand`) + `VehicleBehaviorManager.cs`
(`ApplyRealisticSpeeds`, `IsRecklessDriver`).

Each vehicle gets a **per-vehicle random seed** (`timedRand`, a byte 0–99, re-rolled
periodically — `StepRand`, `ExtVehicleManager.cs:778-786`) that becomes a single scalar
personality knob:

```csharp
// ExtVehicleManager.cs
public void StepRand(ref ExtVehicle extVehicle, bool force) {
    Randomizer rand = Singleton<SimulationManager>.instance.m_randomizer;
    if (force || (rand.UInt32(GlobalConfig.Instance.Gameplay.VehicleTimedRandModulo) == 0)) {
        extVehicle.timedRand = SavedGameOptions.Instance.individualDrivingStyle
                                   ? (byte)rand.UInt32(100)
                                   : (byte)50;
    }
}
```

That single 0–99 value is then read twice, as two opposing personality axes:

```csharp
float egoism = extVehicle.timedRand / 100f;
float altruism = 1f - egoism;
```

`egoism` drives "how much this driver optimizes for themselves"; `altruism` drives "how
courteous/patient this driver is." Six DLS behavior parameters are then linearly
interpolated (`Mathf.Lerp`) between config-defined min/max bounds using egoism or
altruism as the blend factor (`ExtVehicleManager.cs:806-836`, defaults in
`State/ConfigData/DynamicLaneSelection.cs`):

| Parameter | Low end | High end | Blended by | Meaning |
|---|---|---|---|---|
| `maxReservedSpace` | 0 (egoistic) | 5 / 50 for reckless (altruistic) | altruism | how much of the target lane must be clear before a lane-change is attempted |
| `laneSpeedRandInterval` | 0 (altruistic — sees true lane speed) | 25 (egoistic — "imagines being in the slowest queue", cites [BBC: why other queues move faster](http://www.bbc.com/future/story/20130827-why-other-queues-move-faster)) | egoism | noise added when comparing lane speeds |
| `maxOptLaneChanges` | 1 (altruistic) | 3 (egoistic) | egoism | how many lane changes ahead the driver will plan |
| `maxUnsafeSpeedDiff` | 0.1 (altruistic) | 1.0 (egoistic) | egoism | how much slower a "still acceptable" lane change target may be |
| `minSafeSpeedImprovement` | 5 km/h (egoistic) | 30 km/h (altruistic) | altruism | speed gain needed before bothering to change lanes |
| `minSafeTrafficImprovement` | 5% (egoistic) | 30% (altruistic) | altruism | traffic-volume gain needed before bothering to change lanes |

Comment in source (`ConfigData/DynamicLaneSelection.cs`) makes the egoism/altruism
direction explicit for each knob — worth reading directly since some are inverted
relative to intuition (e.g. *low* reserved space = *egoistic*, because an egoistic
driver squeezes into gaps altruistic drivers would leave clear).

**Speed personality** is a separate, simpler formula (`VehicleBehaviorManager.cs:1811-1826`):

```csharp
public float ApplyRealisticSpeeds(float speed, ushort vehicleId, ref ExtVehicle extVehicle, VehicleInfo vehicleInfo) {
    if (SavedGameOptions.Instance.individualDrivingStyle) {
        float vehicleRand = 0.01f * Constants.ManagerFactory.ExtVehicleManager.GetTimedVehicleRand(vehicleId);
        if (vehicleInfo.m_isLargeVehicle) {
            speed *= 0.75f + (vehicleRand * 0.25f); // a little variance, 0.75 .. 1
        } else if (extVehicle.recklessDriver) {
            speed *= 1.3f + (vehicleRand * 1.7f);   // woohooo, 1.3 .. 3
        } else {
            speed *= 0.8f + (vehicleRand * 0.5f);   // a little variance, 0.8 .. 1.3
        }
    } else if (extVehicle.recklessDriver) {
        speed *= 1.5f;
    }
    return speed;
}
```

`GetTimedVehicleRand` mixes vehicle ID parity with the personality seed
(`(vehicleId % 2) * 50 + (timedRand >> 1)`, `ExtVehicleManager.cs:774-776`) — a cheap
way to decorrelate two vehicles that got the same `timedRand` roll. Net effect,
matching the wiki's summary table (`Individual-Driving-Styles.md`):

| Category | Speed multiplier range |
|---|---|
| Heavy vehicles (ships, aircraft, helicopters, meteors, tornados) | 0.75× – 1.0× |
| Reckless drivers | 1.3× – 3.0× |
| Everyone else | 0.8× – 1.3× |

This multiplier is applied to `maxSpeed` (the *target* speed for that road/lane), not
to acceleration — see §2.3.

### 2.2 Reckless Drivers ("Path Of Evil" / recklessness levels)

Enum (`TLM/TMPE.API/Traffic/Enums/RecklessDrivers.cs`):

```csharp
public enum RecklessDrivers {
    PathOfEvil,      // "Path Of Evil (10%)"
    RushHour,        // "Rush Hour (5%)"
    MinorComplaints, // "Minor Complaints (2%)"
    HolyCity,        // "Holy City (0%)"
}
```

The percentage is realized as a **modulo test on vehicle ID**, not a per-spawn coin
flip (`SavedGameOptions.cs:89-96`):

```csharp
internal int getRecklessDriverModulo() => CalculateRecklessDriverModulo(recklessDrivers);
internal static int CalculateRecklessDriverModulo(RecklessDrivers level) => level switch {
    RecklessDrivers.PathOfEvil      => 10,
    RecklessDrivers.RushHour        => 20,
    RecklessDrivers.MinorComplaints => 50,
    RecklessDrivers.HolyCity        => 10000,
};
```

```csharp
// VehicleBehaviorManager.cs:1830-1849
public bool IsRecklessDriver(ushort vehicleId, ref Vehicle vehicleData) {
    if ((vehicleData.m_flags & Vehicle.Flags.Emergency2) != 0) return true;
    if (SavedGameOptions.Instance.evacBussesMayIgnoreRules && vehicleData.Info.GetService() == ItemClass.Service.Disaster) return true;
    if (SavedGameOptions.Instance.recklessDrivers == RecklessDrivers.HolyCity) return false;
    if ((vehicleData.Info.m_vehicleType & RECKLESS_VEHICLE_TYPES) == VehicleInfo.VehicleType.None) return false;
    return (uint)vehicleId % SavedGameOptions.Instance.getRecklessDriverModulo() == 0;
}
```

Only `VehicleInfo.VehicleType.Car` is eligible (`RECKLESS_VEHICLE_TYPES` constant,
line 38) — trucks/buses/trains never roll reckless via this path. Emergency vehicles
*responding to an emergency* (flag `Emergency2`) are always reckless regardless of the
setting, and evacuation buses can optionally be too.

What "reckless" actually changes, per the wiki (`Reckless-Drivers.md`) and confirmed in
code: ignores speed limits (§2.1 multiplier), priority signs (`HasPriority` short-circuits
are **not** present for reckless — instead `MustCheckSpace` in
`VehicleBehaviorManager.cs:1706-1730` skips the normal junction-block check unless it's
a level crossing), red lights, lane arrows, vehicle/parking restrictions. Still obeys
lane connectors and "enter blocked junction" restrictions.

### 2.3 Speed/acceleration: TM:PE does NOT touch acceleration physics

This is the single most load-bearing finding for our "sluggish acceleration" problem.

Grepping `VehicleBehaviorManager.cs` (3074 lines) turns up **zero** references to
acceleration, braking, or any physics integration — every code path computes a
`maxSpeed` (a target/ceiling), never a rate of change. `CalcMaxSpeed`
(`VehicleBehaviorManager.cs:1741-1805`) composes:

1. Wet-road speed penalty (vanilla: −15%..0%, or TM:PE's "stronger road condition
   effects" option: up to −25%/−30% down to a hard floor speed)
2. Broken-road-condition penalty/bonus (vanilla ±0%..+15%, or stronger-effects variant)
3. `ApplyRealisticSpeeds` — the IDS personality multiplier (§2.1)
4. `Math.Max(MIN_SPEED, maxSpeed)` — hard floor of `8f * 0.2f` = 10 km/h
   (`MIN_SPEED` constant, line 20)

That `maxSpeed` is then handed back to the **vanilla game's own `CarAI`**, which owns
the actual acceleration/braking physics from the vehicle prefab (`m_acceleration`,
`m_braking` fields on `VehicleInfo`) and chases the target speed using vanilla's
built-in curve-slowdown and car-length/momentum model. TM:PE's philosophy, confirmed by
this code path, is: **manipulate what speed a vehicle should be trying to reach; never
touch how fast it can change speed.** The mod that *does* touch those fields is Advanced
Vehicle Options (§4.1) — a clean separation of concerns between "traffic-rule mod" and
"vehicle-physics mod" that CS1's ecosystem settled on.

Constant table for reference (`VehicleBehaviorManager.cs:19-33`, all in "raw" game
velocity units where `8f` = 50 km/h, confirmed by inline comments):

```csharp
public const float MIN_SPEED = 8f * 0.2f;               // 10 km/h
public const float MAX_EVASION_SPEED = 8f * 1f;         // 50 km/h
public const float EVASION_SPEED = 8f * 0.2f;           // 10 km/h
public const float ICY_ROADS_MIN_SPEED = 8f * 0.4f;     // 20 km/h
public const float ICY_ROADS_STUDDED_MIN_SPEED = 8f * 0.8f; // 40 km/h
public const float WET_ROADS_MAX_SPEED = 8f * 2f;       // 100 km/h
public const float WET_ROADS_FACTOR = 0.75f;
public const float BROKEN_ROADS_MAX_SPEED = 8f * 1.6f;  // 80 km/h
public const float BROKEN_ROADS_FACTOR = 0.75f;
```

Speed-limit lookup itself (`SpeedLimitManager.cs:256-278`) is a straight per-lane
cached-array read (`cachedLaneSpeedLimits_[laneId]`), falling back to the network
prefab's `laneInfo.m_speedLimit` if custom speed limits are off. **1.0 game speed unit
= 50 km/h** (confirmed via `SpeedLimitManager.cs` doc comment on `GetGameSpeedLimit`).
No curve/turn-radius slowdown logic exists in TM:PE itself — that's entirely vanilla
`CarAI` territory the mod never touches, per the exhaustive grep above.
*(Speculative: this strongly implies vanilla CS1 already does turn-radius-based
deceleration in the base game's vehicle simulation loop, since TM:PE never needed to
add it — this specific vanilla source wasn't fetched to confirm directly.)*

### 2.4 Junction behavior: priority signs, gap acceptance, wait-timeout

Core entry point: `TrafficPriorityManager.HasPriority` (`TrafficPriorityManager.cs:375-566`),
called from `VehicleBehaviorManager`'s per-frame junction check (`MayChangeSegment`,
around line 1600+).

**Priority sign types**: `Main` (green/priority), `Yield`, `Stop` — cycled by the tool,
default is `Main` for every unsigned approach (`Priority-Signs.md`). If no sign is set
anywhere at a junction, vanilla right-hand/left-hand priority applies (§1).

**Gap acceptance is genuinely time-based, not distance-based** — this matches good
traffic-engineering practice and is worth confirming since our own `JunctionArbiter`
uses the same idea. For the ego vehicle, TM:PE computes `targetTimeToTransitNode =
distance / speed` (only when `simulationAccuracy >= High`,
`TrafficPriorityManager.cs:462-472`). For every other vehicle currently approaching or
leaving through the junction, it computes the same time-to-arrival and compares:

```csharp
// TrafficPriorityManager.cs:733-757 (IsConflictingVehicle)
float timeDiff = Mathf.Abs(incomingTimeToTransitNode - targetTimeToTransitNode);
if (timeDiff > GlobalConfig.Instance.PriorityRules.MaxPriorityApproachTime) {
    // gap is "large enough" -> not conflicting, ignore this vehicle
    return false;
}
```

`MaxPriorityApproachTime = 15f` (seconds; `ConfigData/PriorityRules.cs`) — i.e. if the
two vehicles' estimated arrival times at the conflict point differ by more than 15s,
TM:PE doesn't even consider them in conflict. Within that window, rank is decided by
`onMain`/`incomingOnMain` (priority-sign status) plus direction (`ArrowDirection`) —
code not fully traced here, but the wiki's plain-language description
(`Priority-Signs.md`) matches: main-road traffic always wins; between two non-priority
approaches the vanilla side-of-road rule applies.

Also checked before the time-window logic: a **hard-stopped-vehicle short-circuit** —
if the incoming vehicle isn't moving and its junction-transit-state didn't change
recently, it's immediately marked `Blocked` and excluded as a conflict
(`TrafficPriorityManager.cs:684-704`), preventing gridlocked/parked vehicles from
perpetually blocking others' priority checks.

**Per-vehicle junction FSM** (`VehicleJunctionTransitState` enum,
`TLM/TMPE.API/Traffic/Enums/VehicleJunctionTransitState.cs`): `None → Approach → Stop
→ Leave` (or `Blocked` if leave is obstructed by traffic ahead). At `Yield`/`Stop`
signs, once the vehicle's squared velocity drops below
`MaxYieldVelocity² = 2.5²` (`PriorityRules.cs`), it enters the stop/yield check loop
(`VehicleBehaviorManager.cs` around line 1600-1680):

```csharp
if (sqrVelocity <= MaxYieldVelocity * MaxYieldVelocity) {
    if (SavedGameOptions.Instance.simulationAccuracy <= SimulationAccuracy.VeryLow) {
        return VehicleJunctionTransitState.Leave; // skip priority check entirely on low accuracy
    }
    if (extVehicle.waitTime < GlobalConfig.Instance.PriorityRules.MaxPriorityWaitTime) {
        extVehicle.waitTime++;
        bool hasPriority = prioMan.HasPriority(...);
        if (!hasPriority) return VehicleJunctionTransitState.Stop;
        return VehicleJunctionTransitState.Leave;
    }
    // waited too long -> force through regardless of priority
    return VehicleJunctionTransitState.Leave;
}
```

`MaxPriorityWaitTime = 100` (frames, `ConfigData/PriorityRules.cs`) is TM:PE's
**deadlock-breaker / impatience timeout**: this is exactly the mechanism the wiki's FAQ
describes in plain language — "If traffic on a Yield or Stop road is sat waiting too
long, they'll eventually get annoyed and just drive in to the junction"
(`Priority-Signs.md`). Unlike our sim's gap *shrinking* over time (2.8s → 2.2s), TM:PE's
mechanism is a hard cutoff: full priority enforcement until the frame counter hits 100,
then unconditional `Leave`. `MaxStopVelocity = 0.1` distinguishes a true full stop (for
Stop signs) from just "slow" (Yield only needs `MaxYieldVelocity`).

`SavedGameOptions.simulationAccuracy` is a global LOD knob (`VeryLow`/`Low`/`Medium`/
`High`) that trades priority-check fidelity for CPU — at `VeryLow` the whole gap
computation is skipped and vehicles just go once slow enough. Worth noting as a
possible pattern for our own perf scaling if the city gets large.

**"Enter blocked junction"**: default-off; when off, a vehicle won't advance into a
junction unless the *outgoing* segment has space, checked via `MustCheckSpace`
(`VehicleBehaviorManager.cs:1706-1730`) — reckless drivers only check this for level
crossings, everyone else defers to `JunctionRestrictionsManager.IsEnteringBlockedJunctionAllowed`
if junction-restrictions are enabled, else falls back to a structural check (is it an
actual junction with ≠2 segments, not a one-way in/out node).

**Timed Traffic Lights**: current wiki page is a stub pointing at the (now-dead) old
wiki; not independently verified beyond what's implied by the state-machine code
(`TrafficLight/Impl/TimedTrafficLights*.cs` exist in the tree but weren't read in
depth this pass — flagging as a gap if timed-light phase logic is needed later).

### 2.5 Vehicle Restriction Aggression

Not lane-choice/speed personality, but relevant to "how strongly do rules bind":
vehicle restrictions (banning e.g. trucks from a road) are implemented as a
**pathfinding cost penalty**, not a hard block, with a configurable magnitude
(`Vehicle-Restriction-Aggression.md`):

| Level | Penalty |
|---|---|
| Low | 10 |
| Medium (default) | 100 |
| High | 1000 |
| Strict | Infinity (effectively a hard block, even for reckless drivers) |

For reference, vanilla's own **Old Town** policy penalty is 5, **Heavy Traffic Ban** is
10 — so even TM:PE's "Low" setting already out-penalizes the strongest vanilla policy.
Penalty is cumulative across consecutive restricted segments, and pathfinding gives up
("infeasible") past a cost threshold — high aggression without an alternate route means
**the vehicle never spawns at all**, a failure mode worth guarding against if we ever
add vehicle-type road restrictions.

### 2.6 Dynamic Lane Selection (DLS) mechanics beyond the personality lerp

`FindBestLane` (`VehicleBehaviorManager.cs`, ~500 lines) evaluates, for each reachable
lane 1–4 segments ahead: mean speed, total lane-distance to reach a good position, and
whether a "safe" vs "unsafe" lane change is possible, using the personality-scaled
thresholds from §2.1 (`maxUnsafeSpeedDiff`, `minSafeSpeedImprovement`,
`minSafeTrafficImprovement`, `maxOptLaneChanges`). Notably, the once-live
"reserved space" clear-lane check (comparing `NetLane.GetReservedSpace()` against
`maxReservedSpace`) is now **commented out** in the current source
(`VehicleBehaviorManager.cs` ~line 2375-2390) — i.e. the literal gap-physically-exists
check has been superseded by the mean-speed/lane-distance heuristic, even though the
`maxReservedSpace` personality parameter itself is still computed and stored. Treat
this as **evolving/legacy code**, not a stable API to imitate exactly.

DLS is explicitly called out as CPU-heavy: "adds extra workload to the CPU which might
be problematic on older computers... especially in large cities"
(`Individual-Driving-Styles.md`, `Reckless-Drivers.md` FAQ) — every optional realism
feature in TM:PE trades frame time for behavioral fidelity, a pattern worth keeping in
mind if our sim wants a "quality" slider.

---

## 3. Config defaults worth borrowing as starting points

From `TLM/TLM/State/ConfigData/*.cs` (all are *tunable defaults*, not hardcoded):

```csharp
// AdvancedVehicleAI.cs
LaneRandomizationJunctionSel = 3;
LaneRandomizationCostFactor = 1f;
LaneChangingBaseMinCost = 1.1f;
LaneChangingBaseMaxCost = 1.5f;
LaneChangingJunctionBaseCost = 2f;
JunctionBaseCost = 0.1f;
MoreThanOneLaneChangingCostFactor = 2f;
TrafficCostFactor = 4f;
LaneDensityRandInterval = 20f;
MaxTrafficBuffer = 10;

// DynamicLaneSelection.cs (see §2.1 table for the *Min/*Max personality-lerp pairs)
MaxReservedSpace = 0.5f;
MaxRecklessReservedSpace = 10f;
LaneSpeedRandInterval = 5f;
MaxOptLaneChanges = 2;
MaxUnsafeSpeedDiff = 0.4f;
MinSafeSpeedImprovement = 25f;
MinSafeTrafficImprovement = 20f;
VolumeMeasurementRelSpeedThreshold = 50;

// PriorityRules.cs
MaxPriorityCheckSqrDist = 225f;   // 15m radius, sqr'd
MaxPriorityApproachTime = 15f;    // seconds
MaxPriorityWaitTime = 100;        // frames (impatience/deadlock-breaker)
MaxYieldVelocity = 2.5f;
MaxStopVelocity = 0.1f;
```

---

## 4. Adjacent mods

### 4.1 Advanced Vehicle Options (AVO) — `github.com/MacSergey/AdvancedVehicleOptions`

AVO is the mod that actually edits acceleration. Confirmed directly in
`AdvancedVehicleOptions/VehicleOptions.cs`:

```csharp
public float Acceleration {
    get { return m_prefab.m_acceleration; }
    set { m_prefab.m_acceleration = value; ... }
}
public float Braking {
    get { return m_prefab.m_braking; }
    set { m_prefab.m_braking = value; ... }
}
```

It is a thin UI over the **same `VehicleInfo.m_acceleration`/`m_braking` prefab fields
vanilla `CarAI` already reads** — AVO doesn't reimplement vehicle physics, it exposes
sliders for fields that were previously only settable at asset-import time. This
reinforces §2.3: in the CS1 modding ecosystem, "traffic rules" (TM:PE) and "vehicle
physics" (AVO) are treated as cleanly separate concerns, each mod touching only its
own layer. Per-vehicle-type settings also include max speed, colors, and (data-mod
only) turning/spring/damper/lean/nod physics values — confirms CS1's vehicle model has
a fuller physics vector than just accel/braking/maxspeed, though those extra axes
aren't exposed to normal users.

### 4.2 "Realistic Vehicle Speed" (the mod the task called "Realistic Driving Speeds")

*(Speculative naming: could not find a mod titled exactly "Realistic Driving Speeds";
closest and almost-certainly-intended match is "Realistic Vehicle Speed," Steam
Workshop ID 631930385, now removed from Workshop for content-guideline reasons — this
finding is via web search, not source-read, so treat the specifics below as
lower-confidence.)* Per its description: ±10% random per-vehicle speed variance, plus
lane-dependent speed on highways (inside lane faster, outside lane slower — togglable).
Conceptually this is a strict subset of TM:PE's later Individual Driving Styles
feature (indeed, TM:PE's own wiki says IDS was originally split out of/superseded a
mod-level "Realistic speeds" feature bundled in AVO) — another sign the ecosystem
converged on "small random per-vehicle multiplier on target speed" as the standard fix
for vanilla's too-uniform traffic.

### 4.3 CS2's "Traffic" mod by Krzychu124 — `github.com/krzychu124/Traffic`

*(Explored via GitHub's tree API, not deep-read line by line — treat structural
observations as moderate-confidence.)* This is the direct successor to TM:PE for
Cities: Skylines 2, built as a Unity DOTS/ECS mod (`Code/Systems/...Job.cs`,
`Code/Components/...`). Its scope is narrower than TM:PE: lane connectors, priority
signs, turn restrictions, crosswalks — i.e. **junction customization and pathing
tools**, not vehicle behavior/personality systems. This is a meaningful signal: CS2's
own base-game traffic AI is reportedly already better than CS1's vanilla (car-following,
lane discipline), so the community mod hasn't needed to re-invent an
Individual-Driving-Styles-equivalent to make traffic feel alive — the demand for that
kind of mod was specific to CS1's especially primitive vanilla AI. This suggests our
sim, if its IDM/MOBIL/JunctionArbiter core is already sound (see §5), may not need a
CS1-TM:PE-scale overhaul — mainly the "add per-vehicle personality" layer that CS1
vanilla was missing entirely and that CS2 apparently ships out of the box.

---

## 5. Lessons for our sim

Grounded against our current implementation (`src/Domain/Traffic/Idm.cs`,
`TrafficSim.cs`, `JunctionArbiter.cs`, `LaneChange.cs`, surveyed for this report):
our sim already has an IDM car-following model (`A=2.6 m/s²`, `B=2.8 m/s²`,
`T=0.95s`, `S0=2m`), a genuine time-to-arrival gap-acceptance junction arbiter with
impatience-based gap-shrinking (2.8s→2.2s over 6s) and a 6s deadlock-breaker, and a
MOBIL-lite dynamic lane-changer with mandatory-lane-change zones. This is
*already more sophisticated than what vanilla CS1 has* — TM:PE had to build gap
acceptance and dynamic lane selection from scratch because vanilla CS1 had neither;
we start from a better baseline. The actionable gaps, ranked by how directly they
target "sluggish, non-reactive, too-slow-to-accelerate":

1. **We have zero per-vehicle personality/variance — this is the single biggest gap
   vs. TM:PE and is likely the main source of "non-reactive" feel.** Every vehicle in
   our sim uses identical IDM constants and identical desired speed. TM:PE's fix
   (§2.1) is cheap: give each vehicle one random scalar (`egoism`/`timedRand`
   equivalent) at spawn, then derive a target-speed multiplier from it (their range:
   0.8×–1.3× for ordinary vehicles, no change to accel/decel physics at all). Applying
   an analogous multiplier to our IDM's `v0` (desired speed) per vehicle — not to `A`/`B`
   — would immediately produce differentiated behavior (overtaking, gap-closing at
   different rates, visible personality) without touching the physics model that's
   presumably already tuned. This directly mirrors TM:PE's philosophy: **vary the
   target, not the physics.**

2. **Consider varying the MOBIL lane-change gain threshold (currently a flat `0.3`)
   per vehicle**, the same way TM:PE lerps `minSafeSpeedImprovement`/
   `minSafeTrafficImprovement`/`maxUnsafeSpeedDiff` by egoism/altruism (§2.1 table).
   A flat threshold means every vehicle changes lanes at exactly the same trigger
   point — a subtle source of the "robotic" feel even though our lane-changer is
   already dynamic (unlike vanilla CS1's fixed-at-spawn lane choice).

3. **Investigate whether `EnforceNoPenetration`'s back-solve hack is manufacturing
   "sluggish" stops that never show up in telemetry.** TM:PE's equivalent safety net
   (the commented-out reserved-space check, §2.6) was explicitly *retired* in favor of
   a forward-looking heuristic rather than an after-the-fact position clamp — worth
   checking whether our post-hoc clamp is silently forcing hard decelerations that
   look like "sluggishness" from outside the model, exactly the kind of induced-stop
   risk flagged in our own code comment (`TrafficSim.cs:176-179`).

4. **A recklessness archetype (even a simple boolean, matching TM:PE's `RecklessDrivers`
   modulo-on-ID approach) is cheap and would add visible variety**: 2–10% of vehicles
   with higher desired speed (TM:PE: 1.3×–3.0×) and looser gap-acceptance (skip/shrink
   `AcceptedGap` faster) — directly reuses machinery we already have (impatience
   shrinking in `JunctionArbiter`) rather than requiring new mechanisms.

5. **TM:PE's time-window gap-acceptance (`MaxPriorityApproachTime = 15s`) is coarser
   than ours** (a straight ±15s "ignore if arrival times are far apart," vs. our
   `AcceptedGap` of 2.8s shrinking to 2.2s) — ours is the more realistic model
   (TM:PE's is a CPU-saving simplification for CS1's larger, laggier vehicle counts).
   No change recommended here; noted only so we don't regress toward TM:PE's coarser
   model in the name of "matching a reference implementation."

6. **TM:PE's hard `MaxPriorityWaitTime` deadlock-breaker (100 frames, unconditional)
   is a simpler and more legible mechanism than gradual gap-shrinking** — consider
   whether our smooth 2.8s→2.2s taper is actually distinguishable to a player from a
   hard cutoff, or whether it's added complexity without perceptual payoff. Not an
   urgent change; flagged for future simplification discussion.

7. **AVO's separation of "traffic rules" vs. "vehicle physics" as two independently
   tunable layers is a good API-boundary precedent**: if we ever expose acceleration
   tuning to designers/config, keep it as a separate knob from target-speed/personality
   logic, the way CS1's ecosystem split TM:PE (rules) from AVO (physics) — makes each
   layer easier to reason about and test independently. *(Speculative extrapolation
   from ecosystem structure, not a concrete finding from either mod's code alone.)*

8. **CS2's `Traffic` mod not re-implementing IDS-equivalent features is a useful
   signal, not proof of anything**: it may mean CS2's base traffic AI ships
   personality/variance already, or it may mean the mod is just younger/smaller in
   scope than TM:PE was at CS1's end of life. Treat as weak evidence only
   *(speculative — the CS2 base game's own vehicle-AI source is closed and wasn't
   examined)*.

---

## Appendix: file/page index used

TMPE source (raw.githubusercontent.com/CitiesSkylinesMods/TMPE/master/...):
- `TLM/TLM/Manager/Impl/VehicleBehaviorManager.cs`
- `TLM/TLM/Manager/Impl/ExtVehicleManager.cs`
- `TLM/TLM/Manager/Impl/TrafficPriorityManager.cs`
- `TLM/TLM/Manager/Impl/SpeedLimitManager.cs`
- `TLM/TLM/Manager/Impl/JunctionRestrictionsManager.cs`
- `TLM/TLM/State/SavedGameOptions.cs`
- `TLM/TLM/State/ConfigData/{DynamicLaneSelection,AdvancedVehicleAI,PriorityRules,Gameplay,PathFinding}.cs`
- `TLM/TMPE.API/Traffic/Enums/{RecklessDrivers,VehicleJunctionTransitState}.cs`

TMPE wiki (github.com/CitiesSkylinesMods/TMPE/wiki, cloned from `TMPE.wiki.git`):
`Individual-Driving-Styles`, `Dynamic-Lane-Selection` (stub, points to dead old wiki),
`Reckless-Drivers`, `Priority-Signs`, `Speed-Limits`, `Realistic-Speeds` (obsolete
redirect page), `Vehicle-Restriction-Aggression`, `Lane-Changes` (stub), `Stay-in-Lane`,
`Vanilla-capacities`, `Nodes,-Segments,-Lanes`, `Priority-Routes`, `High-Priority-Roads`,
`Advanced-AI` (stub, points to dead old wiki), `Enter-Blocked-Junctions`,
`Junction-Restrictions`, `Highway-Junction-Rules`, `Timed-Traffic-Lights` (stub),
`Vehicle-Flags`.

Adjacent mods: `github.com/MacSergey/AdvancedVehicleOptions` (`VehicleOptions.cs`),
`github.com/krzychu124/Traffic` (tree structure only), Steam Workshop page for
"Realistic Vehicle Speed" (ID 631930385, via web search — not source-read).

Dead ends: `tmpe.viathinksoft.com` (old TM:PE wiki host) returns 403/SNI errors or a
German "page does not exist" placeholder for every page checked
(`Advanced_Vehicle_AI`, `Dynamic_Lane_Selection`, `Realistic_speeds`,
`Timed_traffic_lights`) — this content is likely permanently lost or would need a
Wayback Machine deep-dive beyond this pass's scope.
