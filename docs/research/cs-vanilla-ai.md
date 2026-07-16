# How vehicle/traffic AI actually works in Cities: Skylines 1 & 2 (vanilla)

Research report, 2026-07-16. Purpose: concrete mechanics from CS1/CS2 to tune our
traffic sim, whose cars currently feel "sluggish, non-reactive, accelerate too slowly"
compared to CS.

Method: five parallel web-research passes over developer sources (Colossal Order's
2015 Gamasutra deep dive, the 2023 CS2 Traffic AI dev diary, Game Developer interviews),
modding documentation (cslmodding.info, TM:PE wiki incl. Wayback snapshots of the old
ViaThinkSoft wiki), and community/technical threads. Every claim below carries a source;
anything not verifiable from a primary/authoritative source is marked **SPECULATION**.

---

## 1. CS1 (2015) mechanics

### 1.1 Movement model: spline-following + conflict-point prediction, not car-following

- Vehicles are moved along **pre-computed splines** through road-segment target points;
  they "sample their speed limit from the road type of the segment they are navigating
  upon." There is no continuous force-integration or IDM-style car-following model.
  — CO deep dive: [Game Design Deep Dive: Traffic Systems in Cities: Skylines](https://www.gamedeveloper.com/design/game-design-deep-dive-traffic-systems-in-i-cities-skylines-i-) (Lehto/Morello/Korppoo, 2015)
- Collision avoidance is **predictive at path-crossing points**, first-come-first-served:
  "When two different vehicle segments intersect, we know there is going to be a
  collision so the velocity and the distance to the intersection point is used to
  determine who makes it and who needs to brake." So CS1 is neither classic gap-keeping
  car-following nor block/segment reservation — it's *predict the conflict point, let
  whoever arrives first win, brake the loser*. — same source; corroborated by
  [How Traffic Works in Cities: Skylines](https://www.gamedeveloper.com/design/how-traffic-works-in-cities-skylines) (Tommy Thompson, 2020)
- **The traffic simulation ticks 4 times per second**; rendering interpolates between
  simulation steps for visual smoothness. — Thompson article, same as above.
- Vehicles recognize traffic lights and "will slow down and stop if the lights dictate
  they can't travel into the intersection" (qualitative only; no lookahead-distance
  constant or soft/hard braking tiers are documented anywhere public). — same source.

### 1.2 Speed/acceleration model

- Per-vehicle-asset fields on `VehicleInfo` ([cslmodding.info/modtools](https://cslmodding.info/modtools/)):
  - `m_maxSpeed` — "1 equals 6.25 km/h" (so a car with `m_maxSpeed = 8` tops out at 50 km/h). This is the one solid documented unit conversion.
  - `m_acceleration` — "Controls how quickly the vehicle increases speed, may also affect max speed." **Unitless modifier; no defaults or units documented.**
  - `m_braking` — "Controls how quickly the vehicle decreases speed." Same caveat.
  - `m_turning` — cornering; `m_leanMultiplier`/`m_nodMultiplier`/`m_springs`/`m_dampers` are *cosmetic* body-lean/suspension only.
- World scale: "One cell is 8 × 8 meters"; CS1's coordinate space is effectively metric
  (1 game unit ≈ 1 m). — [cslmodding.info/scale](https://cslmodding.info/scale/)
- **SPECULATION**: a fan time/distance calculator gist assumes accel = 8 m/s²,
  decel = 16 m/s², max ≈ 33.3 m/s. These are the gist author's illustrative numbers,
  *not* confirmed decompiled constants — do not treat as game data.
- Notably, TM:PE's public API only exposes speed overrides (`CalcMaxSpeed`,
  `ApplyRealisticSpeeds` in `IVehicleBehaviorManager`) — **no acceleration/braking hook
  at all**. The most invasive traffic mod ever written tunes *speed caps*, never the
  accel curve; the curve is baked into `CarAI`. — [TMPE.API source](https://raw.githubusercontent.com/CitiesSkylinesMods/TMPE/master/TLM/TMPE.API/Manager/IVehicleBehaviorManager.cs)

### 1.3 Lane usage — why vanilla traffic picks one lane

- **The entire route including individual lanes is chosen once, at spawn**: "Vehicles
  trying to drive from one location to another will select their route, including the
  choice of lanes, at the outset of the journey... always the fastest legal route...
  it takes no account of other traffic. Vehicles will not change their planned path, or
  even their planned lanes, if they encounter traffic." Re-path only happens if the
  network is edited so the path becomes invalid. — [skylines.paradoxwikis.com/Traffic](https://skylines.paradoxwikis.com/Traffic)
- "Vehicles choose their lane quite early, and they choose and stay in the lane that
  allows them to turn to the direction they will be taking." CO **experimented with
  mid-segment lane changes and removed them** — "during internal testing, it actually
  made everything worse." — CO deep dive (via Thompson article).
- Mechanism of the one-lane funnel: lane changes can only occur **at network nodes**;
  vehicles take the lane that leads closest to the side needed for their next turn (and
  the turn after that), as early as possible. Long node-free stretches lock everyone
  into the same "best" lane. — [Steam community technical thread](https://steamcommunity.com/app/255710/discussions/0/3182216552785351834/); the vanilla static-lane-committing pathfinder is confirmed in the archived TM:PE wiki: "the base game utilizes a (mostly) static path-finding algorithm... [paths] also predetermine which individual lanes will be taken." — [archived TM:PE Dynamic Lane Selection page](https://web.archive.org/web/20230424171312/https://tmpe.viathinksoft.com/wiki/index.php?title=Dynamic_Lane_Selection)
- What TM:PE added (revealing what vanilla lacks): per-vehicle **randomized lane
  preference** at every junction ("better spread of vehicles among the available
  lanes"), a per-segment cost = estimated traversal time, and lane-change cost factors
  proportional to lanes crossed. Vanilla has none of this — its lane choice is
  deterministic, so all vehicles converge on the identical "optimal" lane.
  — [archived TM:PE Advanced Vehicle AI page](https://web.archive.org/web/20220526105025/https://tmpe.viathinksoft.com/wiki/index.php?title=Advanced_Vehicle_AI)
- TM:PE's Dynamic Lane Selection thresholds (mod values, useful as reference for a
  *working* lane-change policy): change lane only if ≥15% faster and/or expected speed
  loss ≤10%; forced merges before a lane ends are staggered 50% / 33.3% / 16.7% at
  2 / 1 / 0 segments before the last opportunity. — archived DLS page above.

### 1.4 Junction yielding

- Vanilla fallback right-of-way with no signs: side-of-road rule — "Vehicles drive on
  the Right? Traffic approaching from the left has priority" (and mirrored for LHD).
  Not distance-based. — [TM:PE wiki: Priority Signs](https://github.com/CitiesSkylinesMods/TMPE/wiki/Priority-Signs)
- The popular "nearest-to-junction-wins" folk explanation was **not found** in any
  TM:PE/CO source — treat as unverified. (At *path-conflict* level the deep-dive's
  velocity+distance first-come-first-served rule applies, which may be the origin of
  the folk version.)
- Waiting timeout: "If traffic on a Yield or Stop road is sat waiting too long, they'll
  eventually get annoyed and just drive into the junction" — a patience/timeout escape
  hatch rather than strict right-of-way. — TM:PE Priority Signs page above.
- Vanilla junction control is coarse: the Mass Transit-era priority-road tool "puts stop
  signs on connecting roads" and **cannot add yield signs**. — [TMPE issue #542](https://github.com/CitiesSkylinesMods/TMPE/issues/542)

### 1.5 Despawn behavior

- Congested vehicles are removed: "if cars get stuck in long vehicle queues they are
  eventually removed from the game in order to keep everything healthy" — explicitly a
  CPU-management measure. Passengers/goods "magically teleport... wherever they need to
  go." — [archived TM:PE Toggle Despawn](https://web.archive.org/web/20191118120808/https://tmpe.viathinksoft.com/wiki/index.php?title=Toggle_despawn), [current TM:PE wiki](https://github.com/CitiesSkylinesMods/TMPE/wiki/Toggle-Despawn)
- Implementation detail from a modder working off decompiled source: the despawn check
  lives in `CargoTruckAI` and `PassengerCarAI`; "They check for the Congestion flag to
  despawn (in SimulateStep)." — [Paradox forum, "Prevent cars from despawning" mod thread](https://forum.paradoxplaza.com/forum/threads/release-prevent-cars-from-despawning-when-they-are-blocked-for-too-long.843996/)
- Gridlock variant: "If a vehicle has found itself in gridlock and unable to move, it
  will actually teleport back to its origin." (Possibly a distinct mechanism from the
  queue-timeout despawn; sources conflate them.) — Thompson article.
- **SPECULATION / unverified numbers**: an `m_blockCounter` threshold of 150 (service)
  / 100 (others) circulated in search summaries but could not be confirmed in any
  directly-fetched source. No exact seconds/minutes timer is published anywhere.
- CS1 has a hard cap of ~16,384 active vehicle instances (community-documented limit,
  widely cited in pocket-car discussions). — [Steam thread on pocket cars](https://steamcommunity.com/app/255710/discussions/0/3148519255762521117/)

### 1.6 Parking ("pocket cars")

- Vanilla has **no real parking**: "cims sometimes spawn a car at their current position
  or... cars suddenly disappear... This (mis)behavior is also known as 'using pocket
  cars'... the base game does not come anywhere near real-life behavior when talking
  about parking." Spawning a pocket car (or arriving from an outside connection) are the
  *only* ways a car comes into existence; cims only re-enter their own car "if it
  coincidentally lied along their designated route."
  — [archived TM:PE Parking AI page](https://web.archive.org/web/20220407013304/https://tmpe.viathinksoft.com/wiki/index.php?title=Parking_AI&oldid=458)

### 1.7 Driver uniformity ("robotic" feel)

- "In the vanilla game, the only difference between drivers is the vehicle asset they
  drive... its effects on traffic are still minimal." All cims uniformly obey rules;
  TM:PE added Reckless Drivers and Individual Driving Styles (heavy vehicles −10..0%,
  reckless +10..+60%, others −20..+30% of speed limit) precisely to break this
  uniformity. — [TM:PE wiki: Individual Driving Styles](https://github.com/CitiesSkylinesMods/TMPE/wiki/Individual-Driving-Styles), [Reckless Drivers](https://github.com/CitiesSkylinesMods/TMPE/wiki/Reckless-Drivers)
- Vanilla road policies already use *soft costs*, not hard bans: Old Town policy =
  pathfinding penalty 5, Heavy Traffic Ban = penalty 10. — [TM:PE wiki: Vehicle Restriction Aggression](https://github.com/CitiesSkylinesMods/TMPE/wiki/Vehicle-Restriction-Aggression)

---

## 2. CS2 (2023) mechanics

Primary source: Colossal Order, **Development Diary #2: Traffic AI** (June 26, 2023) —
[forum.paradoxplaza.com](https://forum.paradoxplaza.com/forum/threads/development-diary-2-traffic-ai.1591141/),
mirrored at [colossalorder.fi](https://colossalorder.fi/?p=1597) and
[paradoxinteractive.com](https://www.paradoxinteractive.com/games/cities-skylines-ii/features/traffic-ai).
Word-of-god quotes also from the [Game Developer interview with Hallikainen & Lehto](https://www.gamedeveloper.com/design/how-colossal-order-turned-roads-into-the-backbone-of-cities-skylines-ii).

### 2.1 Cost-based pathfinding (the core change vs CS1)

- "Agents choose a route based on a pathfinding cost... calculated using multiple
  factors such as the city's road network, traveling time, travel cost, agent
  preferences, and more." Four named cost components: **Time, Comfort, Money,
  Behavior**. (CO never says "cost graph" — that's a community gloss.)
  - **Time** — "usually the most important"; a longer highway route can beat a shorter street route.
  - **Comfort** — penalizes "unnecessary turns at intersections"; includes finding parking / a good transit stop.
  - **Money** — fuel, parking, fares; cargo value scales with distance.
  - **Behavior** — "agents' willingness to make 'dangerous' decisions in traffic, such as making a U-turn."
- **Per-demographic weighting**: teens weight Money, adults weight Time, seniors weight
  Comfort. "Aggression" is thus a *cost discount on risky maneuvers per agent type*
  (emergency vehicles "have a more lenient behavior model... make dangerous pathfinding
  decisions if necessary"; citizens and delivery vehicles are less likely to) — **not**
  per-citizen randomized error injection.
- **SPECULATION**: a granular per-action cost list (jaywalking, lane change, U-turn
  each adding cost) appears in a [Steam modding thread](https://steamcommunity.com/app/949230/discussions/0/601891467285328134/) about the Custom Vehicle
  Pathfind mod — plausible reverse-engineering, not developer-stated.

### 2.2 Dynamic behavior / rerouting (CS1's biggest fix)

- CS1 comparison, direct quote: in CS1 "pathfinding was proximity-based... agents would
  calculate their destinations or order services by straight line distance without
  taking the existing road network into account," and paths were static — agents would
  "stick to it, patiently sitting in a traffic jam."
- CS2: "Agents will adjust their route based on events along the way. They may change
  lanes to avoid a car accident or a stopped service vehicle or make room for a vehicle
  responding to an emergency." Prolonged jams trigger recalculation, "resulting in
  'dangerous' behavior and making U-turns to find alternative routes."
  **No recalculation cadence/threshold is disclosed anywhere.**
- Lane behavior: new cars fill lanes evenly at intersections; vehicles overtake "when
  the simulation notices that the other lanes are less used" (journalist paraphrase of
  the diary — medium confidence). Agents "are always looking for suitable spots to
  improve the traffic flow, by changing lanes or sneaking through an intersection at the
  last minute."
- **Despawn failsafe removed** (Hallikainen, Game Developer interview): in CS1 stuck
  cars "would just vanish and end up at their destination, which was essentially our
  failsafe"; in CS2 "the cars won't simply disappear... they can change lane or even do
  a U-turn and find another way."

### 2.3 Yielding / gap acceptance

- Roundabouts: "Vehicles entering the roundabout give way to those already on it,
  however, just like in real life, vehicles might cut in front of another vehicle
  already on it, if a suitable opportunity arises." — dev diary.
- Emergency vehicles: "If possible, other vehicles will give way... by switching lanes
  on multilane roads." — dev diary.
- **Negative finding (high confidence in the absence)**: no official source describes a
  formal gap-acceptance model — no minimum time-gap, no per-citizen patience scalar.
  CO stays qualitative ("suitable opportunity"). Anything more precise would require
  decompilation.

### 2.4 Accidents ("drivers make mistakes")

- Per-road-segment accident probability driven by road condition, lighting/weather,
  congestion; on trigger "a vehicle on the segment will lose control and be pushed in a
  random direction," chaining into pileups; police/maintenance must physically clear
  wrecks, forcing rerouting around the blocked lane. CO: "accidents on roads are not
  random... there is always a reason behind them." — [cs2.paradoxwikis.com/Traffic](https://cs2.paradoxwikis.com/Traffic), dev diary.
- CO has repeatedly confirmed collisions/mishaps are intentional and staying in vanilla,
  despite community complaints about wrecks that never clear. — [Steam thread](https://steamcommunity.com/app/949230/discussions/0/4406292068724107812/)
- Note: **no CO quote literally says "drivers make mistakes"** as random error
  injection; imperfection = opportunistic risk-taking via the Behavior cost + the
  accident-probability system.

### 2.5 Parallelization

- Dev diary: calculations "take advantage of all the available processing power of the
  multicore CPUs"; "Cities: Skylines II doesn't feature hard limits for agents."
- The ECS/Burst specifics come from a *different* diary ([Code Modding Dev Diary #3](https://www.paradoxinteractive.com/games/cities-skylines-ii/modding/dev-diary-3-code-modding)):
  Unity "Entity Component System or Burst compilation... can increase the speed of some
  calculations up to 30-40 times."
- Post-launch reality: Hallikainen — "We completely overestimated the engine's
  capabilities at the beginning of the project," citing lack of ECS long-running-job
  support. — [PC Gamer](https://www.pcgamer.com/games/sim/cities-skylines-2-boss-says-they-completely-overestimated-the-unity-engines-capabilities/)

### 2.6 Known criticisms (community, sourced)

- **Last-second lane changes / merges**: the dominant CS2 traffic complaint. Every road
  node is evaluated context-free by the pathfinder, so lane-change decisions commit at
  the node itself, producing late multi-lane swerves and backed-up off-ramps (one forum
  poster estimated off-ramp throughput dropping to 20-30% of capacity — **SPECULATION**,
  informal estimate). — [Steam](https://steamcommunity.com/app/949230/discussions/0/601891467285328134/), [Paradox forum](https://forum.paradoxplaza.com/forum/threads/the-commercial-fix-has-highlighted-one-of-the-most-glaring-problems-traffic-ai.1606572/)
- **U-turns crossing double yellows, mid-highway u-turns, vehicles stopping mid-road
  waiting for a U-turn gap** — widely reported; defenders call it "realistic," critics
  note "the rate of people breaking the law is certainly too high." — [Paradox forum](https://forum.paradoxplaza.com/forum/threads/traffic-is-now-dumber-than-ever-before.1607471/)
- **Parking pop-in**: drivers bypassed building lots for roadside parking; cars appear
  to pop in/out near parking structures (placement is asset-anchor-based, not
  continuously simulated). Patched in 1.0.14f1 ("Increased citizen preference to park
  cars on building lots vs roadside"). — [PCGamesN](https://www.pcgamesn.com/cities-skylines-2/fix-traffic-garbage)
- **"Painfully slow" vehicles at scale** are attributed by players to *simulation-rate
  throttling under CPU load* (~100-150k+ population), not to acceleration constants —
  a different failure mode worth distinguishing from ours. — [Steam thread](https://steamcommunity.com/app/949230/discussions/0/4349988819858206257/)
- CO's own "feel" fixes post-launch were **pathfinding-cost changes** (penalize U-turns
  harder, reduce late lane changes — patch 1.5.9f1 "Morning Dew"), not accel/braking
  retunes. — [PCGamesN](https://www.pcgamesn.com/cities-skylines-2/update-morning-dew)
- **No public datamined CS2 acceleration/braking constants exist** (verified absence —
  targeted searches surface only CS1-era material).

---

## 3. What makes CS cars feel "responsive"

Synthesis (interpretation is ours; individual facts sourced above):

1. **They brake only when a predicted conflict demands it.** CS1's model is "cruise at
   segment speed limit unless a conflict point / red light / leading vehicle forces
   braking." There is no standing drag, no cautious default. Cars spend most of their
   time *at* the speed limit.
2. **First-come-first-served conflict resolution keeps traffic assertive.** The winner
   of a conflict point doesn't slow at all; only the loser brakes. Junctions clear
   aggressively rather than both parties hedging.
3. **Patience timeouts prevent visible dithering.** A waiting car eventually "gets
   annoyed and just drives into the junction" — stalls resolve instead of accumulating.
4. **Low sim rate + interpolation, decisions precomputed.** At 4 Hz with spline
   interpolation, per-tick decisions are few and cheap; the *rendered* motion is smooth
   because it's interpolation of a plan, not integration of forces. Responsiveness is
   an animation-layer property as much as a physics one.
5. **CS2's "aliveness" comes from opportunism, not physics**: gap-sneaking at
   intersections, roundabout cut-ins, event-triggered rerouting, U-turns out of jams.
   The cars *do things*; the acceleration model itself is undocumented and apparently
   was never the tuning lever CO reached for.
6. **Game-feel literature** ([Dahl & Kraus 2015](https://dl.acm.org/doi/10.1145/2818187.2818275), partially verified): deceleration
   ramp-time is a highly sensitive lever for perceived responsiveness — ~360 ms of
   difference in decel ramp flips perception between "floaty" and "twitchy." Snappy
   *stopping* matters at least as much as snappy launching.

---

## 4. Numeric values found

| Value | Game/context | Status |
|---|---|---|
| Sim tick rate: 4 updates/sec (rendering interpolated) | CS1 | Confirmed (CO deep dive via Thompson) |
| `m_maxSpeed`: 1 unit = 6.25 km/h | CS1 | Confirmed (cslmodding.info) |
| World scale: 1 cell = 8×8 m; ~1 unit ≈ 1 m | CS1 | Confirmed (cslmodding.info) |
| `m_acceleration`, `m_braking`: unitless per-asset modifiers, no documented defaults | CS1 | Confirmed absence of docs |
| Vehicle instance cap ≈ 16,384 | CS1 | Community-documented |
| Old Town policy penalty = 5; Heavy Traffic Ban = 10 (soft path costs) | CS1 | Confirmed (TM:PE wiki) |
| Despawn: `Congestion` flag checked in `SimulateStep` of `PassengerCarAI`/`CargoTruckAI`; no public timer value | CS1 | Confirmed mechanism, timer unknown |
| `m_blockCounter` 150/100 despawn thresholds | CS1 | **SPECULATION — could not verify** |
| Fan gist accel 8 / decel 16 m/s² | CS1 | **SPECULATION — author's assumption, not decompiled** |
| TM:PE DLS (mod, not vanilla): lane change iff ≥15% faster and/or ≤10% loss; forced merges staggered 50/33.3/16.7% at 2/1/0 segments | CS1 mod | Confirmed (archived TM:PE wiki) |
| TM:PE driving-style speed bands: heavy −10..0%, reckless +10..+60%, others −20..+30% | CS1 mod | Confirmed |
| CS2 acceleration/braking/top-speed constants | CS2 | **None public — verified gap** |
| ECS/Burst "30-40×" speedup on some calculations | CS2 | Confirmed dev quote (modding diary #3) |
| IDM reference: max accel a = 0.8–2.5 m/s² (comfortable band), comfortable decel b ≈ 2–3 m/s², time headway T ≈ 1.5 s, min gap s₀ ≈ 2 m; hard braking up to ~8–9 m/s²; ITE/AASHTO design decel 3.0/3.4 m/s² | general traffic engineering, not CS | Confirmed ([traffic-simulation.de](https://traffic-simulation.de/info/info_IDM.html)) — note: that demo *deliberately* sets a = 0.3 m/s² to induce sluggish stop-and-go, i.e. sub-1 m/s² accel is a known recipe for exactly the feel we're trying to escape |
| Decel-ramp perception threshold ≈ 360 ms (floaty ↔ twitchy) | game-feel study | Partially verified (Dahl & Kraus 2015; direction reliable, exact figure secondhand) |

---

## 5. Lessons for our sim

1. **Raise acceleration into (or above) the comfortable-driving band.** Real cars do
   1-2.5 m/s² comfortably; sluggish-feeling sims are typically sub-1 m/s². CS never
   published its constants, but its *architecture* (cruise at limit, brake only on
   predicted conflict) means cars are effectively always at target speed — emulate the
   outcome: quick ramp to limit, sit there.
2. **Default to assertive, brake by exception.** Adopt CS1's inversion: a car's default
   state is "at segment speed limit"; deceleration happens only for a *specific
   predicted* conflict point, red light, or leading vehicle — never as generalized
   caution. Compute the conflict point, decide a winner (first-arrival by
   velocity+distance), and only the loser brakes.
3. **Make braking snappy and reactive, not a fixed gentle curve.** Perceived
   responsiveness is dominated by decel ramp time (~hundreds of ms matter). Use a
   strong decel toward conflicts (real-world hard braking 3-9 m/s² is visually
   "decisive") and release it immediately when the conflict clears — gap-closing
   IDM-style braking beats a single fixed deceleration constant.
4. **Add a patience timeout at yields.** CS's "gets annoyed and just drives in" rule is
   load-bearing: it converts deadlocks/dithering into visible assertiveness. A waiting
   car that eventually goes reads as alive; one that waits forever reads as broken.
5. **Randomize lane preference per vehicle.** Vanilla CS1's one-lane funnel comes from
   a deterministic lane cost — every car picks the identical optimum. TM:PE's fix
   (per-vehicle random lane bias at each junction + lane-change cost proportional to
   lanes crossed + only change when ≥15% faster) is a proven recipe. Also: decide lane
   changes *earlier* than the final node — CS2's biggest complaint is context-free
   node-local lane commitment causing last-second swerves.
6. **Decouple decision rate from render rate.** CS1 runs decisions at 4 Hz and
   interpolates. Cheap, and it *helps* feel: motion is smooth-by-construction while AI
   stays simple. Our sim doesn't need per-frame physics to feel responsive.
7. **Aggression as a cost modifier, not random error.** CS2 models "human" driving as
   per-agent-type discounts on risky-maneuver costs (U-turn, cut-in, gap-sneak) — cheap
   to add and produces visible opportunism (cars sneaking through gaps reads as
   "reactive"). Avoid CS2's failure mode: don't let risky maneuvers get so cheap that
   mid-road U-turn waits block traffic.
8. **Have a jam escape hatch, but prefer reroute over despawn.** CS1 despawns/teleports
   stuck cars (players hate it, but it protected the sim); CS2 replaced it with
   event-triggered re-pathing + U-turns. If we add rerouting, trigger it on prolonged
   blockage, and keep a last-resort removal for true deadlock.
9. **Diagnose "sluggish" precisely before tuning.** CS2's "painfully slow" complaints
   at scale were sim-rate throttling, not accel constants. Verify our cars' measured
   accel (m/s²) and time-at-speed-limit fraction first; if cars are slow because they
   rarely *reach* the limit (over-cautious braking / conflict over-prediction), fixing
   the accel constant alone won't help.
10. **Don't chase per-frame realism CO themselves rejected.** CO removed mid-segment
    lane changes from CS1 because it "made everything worse," and CS2's more reactive
    lane AI generated the game's loudest criticism. Predictable-but-assertive beats
    clever-but-jittery.

---

## Appendix: all sources

**Developer / official**
- https://www.gamedeveloper.com/design/game-design-deep-dive-traffic-systems-in-i-cities-skylines-i- (CO, 2015)
- https://www.gamedeveloper.com/design/how-traffic-works-in-cities-skylines (Thompson, 2020)
- https://forum.paradoxplaza.com/forum/threads/development-diary-2-traffic-ai.1591141/ (CS2 Traffic AI dev diary, 2023)
- https://colossalorder.fi/?p=1597 · https://www.paradoxinteractive.com/games/cities-skylines-ii/features/traffic-ai (mirrors)
- https://www.paradoxinteractive.com/games/cities-skylines-ii/modding/dev-diary-3-code-modding
- https://www.gamedeveloper.com/design/how-colossal-order-turned-roads-into-the-backbone-of-cities-skylines-ii
- https://www.pcgamer.com/games/sim/cities-skylines-2-boss-says-they-completely-overestimated-the-unity-engines-capabilities/
- https://cs2.paradoxwikis.com/Traffic · https://skylines.paradoxwikis.com/Traffic

**Modding documentation**
- https://cslmodding.info/modtools/ · https://cslmodding.info/scale/ · https://cslmodding.info/asset/vehicle/
- https://github.com/CitiesSkylinesMods/TMPE/wiki — Priority-Signs, High-Priority-Roads, Toggle-Despawn, Individual-Driving-Styles, Reckless-Drivers, Vehicle-Restriction-Aggression
- Archived ViaThinkSoft TM:PE wiki (Wayback): Dynamic_Lane_Selection, Advanced_Vehicle_AI, Parking_AI (oldid=458), Toggle_despawn
- https://raw.githubusercontent.com/CitiesSkylinesMods/TMPE/master/TLM/TMPE.API/Manager/IVehicleBehaviorManager.cs
- https://github.com/CitiesSkylinesMods/TMPE/issues/542 · /issues/28
- https://forum.paradoxplaza.com/forum/threads/release-prevent-cars-from-despawning-when-they-are-blocked-for-too-long.843996/

**Community / criticism**
- https://steamcommunity.com/app/255710/discussions/0/3182216552785351834/ (CS1 one-lane)
- https://steamcommunity.com/app/255710/discussions/0/3148519255762521117/ (pocket cars)
- https://steamcommunity.com/app/949230/discussions/0/601891467285328134/ · /4406291330030559509/ (CS2 pathfinding node criticism)
- https://forum.paradoxplaza.com/forum/threads/traffic-ai-is-very-bad.1624138/ · /traffic-is-now-dumber-than-ever-before.1607471/ · /the-commercial-fix-has-highlighted-one-of-the-most-glaring-problems-traffic-ai.1606572/
- https://steamcommunity.com/app/949230/discussions/0/4349988819858206257/ (CS2 slow-at-scale)
- https://steamcommunity.com/app/949230/discussions/0/4406292068724107812/ (accidents intentional)
- https://www.pcgamesn.com/cities-skylines-2/fix-traffic-garbage · /update-morning-dew

**General traffic-sim / game-feel references (not CS-specific)**
- https://traffic-simulation.de/info/info_IDM.html (IDM parameters)
- https://copradar.com/chapts/references/acceleration.html (ITE/AASHTO decel standards)
- https://dl.acm.org/doi/10.1145/2818187.2818275 (Dahl & Kraus, game-feel accel/decel study)
