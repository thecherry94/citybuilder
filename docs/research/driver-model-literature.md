# Driver-model literature: making IDM drivers feel human and assertive

*Research survey, July 2026. Feeds tuning of our IDM-based traffic sim
(current params: T=0.95 s, s0=2 m, a=2.6 m/s², b=2.8 m/s²; measured KPIs: startup lost
time 1.33 s, saturation headway 3.52 s, gap acceptance 2.8→2.2 s, fixed turn speeds
9–10 m/s). Sources were gathered by five parallel web-research passes and an adversarial
verification pass (key numeric claims independently re-checked; verdicts folded in below);
confidence flags and speculation markers are inline.*

---

## 1. Why plain IDM discharges queues slowly

### 1.1 The model, and what happens at standstill

IDM acceleration and desired gap:

```
dv/dt = a · [ 1 − (v/v0)^δ − (s*/s)² ]
s*(v, Δv) = s0 + v·T + v·Δv / (2·√(a·b))
```

Three structural facts drive queue-discharge behaviour:

1. **At v = 0 the entire velocity-dependent part of s\* vanishes** — s\* = s0 exactly,
   regardless of Δv. The √(a·b) interaction term contributes *nothing* to the restart
   decision. Restart acceleration is therefore purely
   `a·(1 − (s0/s)²)` ([Wikipedia: IDM](https://en.wikipedia.org/wiki/Intelligent_driver_model);
   algebraic consequence, high confidence).
   With cars queued at their standstill gap s ≈ s0, restart acceleration is **≈ 0 until the
   leader physically opens the gap**. For our params (a=2.6, s0=2): the follower only reaches
   half its max acceleration once the gap has grown to s0·√2 ≈ 2.8 m, and 75 % of max at
   s = 2·s0 = 4 m. Each vehicle must wait for its leader to *move away ~1–2 m* before pulling
   meaningful acceleration — this compounds down the queue into a slow discharge wave.
   Real drivers instead begin moving on the *visual cue* that the leader has started
   (see §3.2).

2. **δ only matters near v0.** The free term `1 − (v/v0)^δ` is ≈ 1 at low speed for any δ;
   δ=4 (default) vs δ=1 changes how acceleration *tapers off* approaching desired speed
   (δ→∞ = Gipps-like constant-then-cutoff; δ=1 = OVM-like early fade)
   ([Zhou et al. 2025, "Twenty-Five Years of the IDM"](https://arxiv.org/pdf/2506.05909), high confidence).
   Raising δ will **not** fix slow queue discharge from standstill; it *does* make the
   mid-speed portion of the launch (say 30–50 km/h toward a 50 km/h limit) noticeably punchier.

3. **The superposition of free and interaction terms drags acceleration below both bounds.**
   Even when the gap equals the "desired" gap, the free term is still being debited by
   (s*/s)² = 1, so equilibrium gap is inflated above s0+vT:
   `s_eq(v) = (s0 + vT) / √(1 − (v/v0)^δ)`.
   Schakel/van Arem/Netten put it bluntly: with realistic parameters plain IDM's capacity
   comes out *"just below 1900 veh/h"* and *"in order to reach a reasonable capacity, the
   desired time headway needs to be lowered to unreasonable values"*
   ([Schakel, van Arem, Netten 2010](https://ieeexplore.ieee.org/document/5625133), read directly, high confidence).

### 1.2 Known fixes in the literature

| Fix | What it changes | Effect | Source |
|---|---|---|---|
| **IDM+ / IIDM** (Schakel et al. 2010/2012; Treiber's "Improved IDM") | `dv/dt = a·min[1−(v/v0)^δ, 1−(s*/s)²]` — take the **minimum** of free and interaction terms instead of their superposition | Equilibrium gap becomes exactly `s0 + vT` (triangular fundamental diagram); realistic capacity with realistic T; followers can actually reach v0 in homogeneous flow | [Schakel et al. 2010](https://ieeexplore.ieee.org/document/5625133); [Salles et al. 2020 (SUMO EIDM paper)](https://sumo.dlr.de/2020/SUMO2020_paper_28.pdf) — high confidence |
| **ACC model / CAH blend** (Kesting, Treiber, Helbing 2010) | Blend IDM with the Constant-Acceleration Heuristic via "coolness factor" c ∈ [0.95, 1.0] (they use 0.99). CAH assumes the leader *keeps its current acceleration* instead of IDM's worst-case "leader may brake fully at any instant" | Removes IDM's over-braking in tight-but-safe situations: in their cut-in test plain IDM brakes at up to 8 m/s² where the ACC model never exceeds b = 2 m/s², with a marginally *better* outcome. Cars read as confident instead of skittish | [Kesting, Treiber, Helbing 2010, arXiv:0912.3613](https://arxiv.org/pdf/0912.3613) — read directly, high confidence. Note: CAH alone degenerates (gives 0 deceleration when Δv=0, a_l=0), it is only usable as a blend |
| **EIDM startup ramp** (Salles, Kaufmann, Reuss 2020; SUMO's "EIDM" model) | Multiplies a_max by a tanh ramp `a_korr(t)` over a tunable time-to-max-acceleration t_amax after drive-off; plus explicit handling of drive-off at signals/junctions | Matches drone-measured drive-off trajectories at a Stuttgart signal (1050 trajectories, 540 standing starts). Keeps jerk < 1.5 m/s³ | [Salles et al. 2020](https://sumo.dlr.de/2020/SUMO2020_paper_28.pdf) — read directly, high confidence |
| **Intersection-specific recalibration** (Kovács/Bolla et al. 2016, Technical Gazette 23(5)) | Calibrate IDM against measured signal discharge headways | Signalized-intersection IDM needs **~30 % higher a and ~30 % lower T** than motorway values ("differ… by approximately 30 % in each case" — abstract verified); companion paper operationalizes this as T = 1.0 s at signals vs 2.0 s on motorway | [hrcak.srce.hr/167511](https://hrcak.srce.hr/167511) (open-access, abstract confirms; PDF bot-blocked) + [Kovács CARLA companion paper](https://gradus.kefo.hu/archive/2021-3/2021_3_CSC_001_Kovacs.pdf) — high confidence on direction/magnitude, exact calibrated a/T values not extracted |
| **Situation-dependent parameter sets** (Kesting et al. 2008) | Different ACC/IDM parameters for free flow, congested, upstream/downstream fronts, bottleneck | "The main impact found was an increased capacity" | via [Schakel et al. 2010](https://ieeexplore.ieee.org/document/5625133) — high confidence |
| **Stochastic IDM / action points** (Treiber & Kesting 2017) | Add noise + discrete perception "action points" (Wiedemann-flavoured) to IDM | Reproduces the empirically *concave* growth of platoon oscillations that deterministic IDM gets wrong (it predicts convex growth) | [Treiber & Kesting 2017](https://www.sciencedirect.com/science/article/abs/pii/S0191261517307014) via [Zhou et al. 2025](https://arxiv.org/html/2506.05909v1) — medium confidence |

### 1.3 Important nuance: IDM's restart is wrong in *two directions*

The literature splits — worth keeping straight when tuning:

- **Too abrupt at t=0, too tailgatey overall (default freeway params):** Treiber & Helbing
  (2004) found *human drivers leave larger gaps when driving off than the IDM predicts*, and
  the EIDM authors note IDM's acceleration can jump discontinuously to a_max at the drive-off
  instant (unrealistic jerk) ([Salles et al. 2020](https://eclipse.dev/sumo/documents/2020/SUMO2020_paper_28.pdf), high confidence —
  note the 2004 reference is the German book chapter "Visualisierung der fahrzeugbezogenen
  und verkehrlichen Dynamik…", *not* the 2003 memory-effects Phys. Rev. E paper).
- **Too sluggish in sustained discharge (what we're seeing):** with motorway-style parameter
  sets (a as low as 0.73 m/s², T ≈ 1.5–1.6 s — the canonical Treiber 2000 set), IDM badly
  undershoots intersection discharge; the intersection-calibration literature fixes this with
  higher a / lower T (Kovács 2016), and recent work notes plain IDM "delays the follower's
  acceleration until the leader reaches its desired speed and creates unnecessarily large
  gaps" ([SEIDM paper via search](https://arxiv.org/abs/2605.23915), medium confidence).

Both are consequences of the same structure: no anticipation, worst-case braking assumption,
and the superposition term. The fixes above address them jointly.

### 1.4 Diagnosing *our* 3.52 s saturation headway *(our derivation — marked speculation)*

Steady-state IDM headway at the stop line should be
`h = (s_eq(v) + L_veh) / v` with `s_eq = (s0+vT)/√(1−(v/v0)^δ)`.
For our params at v = 9.5 m/s, v0 = 13.9 m/s, L ≈ 4.5 m:
s_eq ≈ (2 + 9.03)/0.885 ≈ 12.5 m → **h ≈ 1.8 s**. Even at v = 7 m/s it's ≈ 1.9 s.

So with T=0.95 and a=2.6, *theoretical* IDM saturation headway for us is ~1.8–1.9 s — the
measured 3.52 s is ~1.7 s worse than the model's own steady state. **Speculation:** the gap
is dominated by transient/implementation effects rather than the parameter set — candidates:
(a) the standstill restart wave of §1.1(1) never converging within our typical queue lengths
(Bonneson's field data shows real queues converge by vehicle 4–6; ours may still be far from
steady state at the stop line), (b) junction entry logic (gap checks, turn-speed caps at
9–10 m/s) re-inserting spacing, (c) measurement window including starved intervals.
Recommended first step: log per-queue-position discharge headways (h₁, h₂, … h₉) and compare
against Bonneson's empirical decay pattern (§2.3) before changing parameters.

---

## 2. Realistic parameter ranges and the macroscopic KPI mapping

### 2.1 IDM parameter ranges from the literature

| Param | Canonical freeway (Treiber 2000) | traffic-simulation.de "realistic" note | Urban calibration studies | Our value |
|---|---|---|---|---|
| a (max accel) | 0.73 m/s² | "realistic values are **0.8 to 2.5 m/s²**" (demo uses 0.3 deliberately to provoke jams) | 1.31–2.05 m/s² per-driver (MDPI 2025 urban); intersections need ~30 % *higher* than motorway (Kovács 2016); stability rule of thumb a > 1.5 m/s² and a ≥ s0/T² | **2.6** — already at the assertive end; fine |
| b (comfort decel) | 1.67 m/s² | "realistic values are around **2 m/s²**" | 1.06–5.93 m/s² per-driver spread; CARLA study found b=2.0 already "too high" for smooth signal stops, preferring 1.0 with T=2.0 | **2.8** — high; combined with IDM's worst-case braking term this amplifies over-braking (§1.2 CAH row) |
| T (time headway) | 1.5–1.6 s | "realistic values vary between **2 s and 0.8 s** and even below" (German driving-school 1.8 s) | T ≈ 1.0 s calibrated *at signals* vs 2.0 s motorway (Kovács); Punzo 2015: **T is the single most influential IDM parameter** | **0.95** — matches the signal-calibrated value; not the problem |
| s0 (min gap) | 2.0 m | 2.0 m ("kept at standstill, also in red-light queues") | real drivers 2.3–3.3 m (MDPI 2025) | **2.0** — fine |
| δ | 4 | 4 | — | check ours; δ=4 is standard |

Sources: [traffic-simulation.de IDM page](https://traffic-simulation.de/info/info_IDM.html) (Treiber's own site — high confidence);
[Zhou et al. 2025 review](https://arxiv.org/pdf/2506.05909); [MDPI 2025 IDM calibration](https://www.mdpi.com/2673-7590/5/2/57) (medium);
[Kovács CARLA paper](https://gradus.kefo.hu/archive/2021-3/2021_3_CSC_001_Kovacs.pdf) (high);
[Albeaik et al. 2022, Table 1](https://arxiv.org/pdf/2104.02583) (high).

For reference, other simulators' launch-relevant defaults:

| Simulator | Model | Accel | Decel | Headway | Standstill accel | Min gap |
|---|---|---|---|---|---|---|
| SUMO (passenger) | Krauss | 2.6 m/s² | 4.5 m/s² (hard cap) | tau = 1.0 s | = accel | 2.5 m |
| VISSIM | Wiedemann 99 | CC9 = 1.5 m/s² @ 80 km/h | (desired decel fns) | CC1 = 0.9 s | **CC8 = 3.5 m/s²** | CC0 = 1.5 m |

Sources: [SUMO vehicle-type defaults](https://sumo.dlr.de/docs/Vehicle_Type_Parameter_Defaults.html),
[SUMO vType docs](https://sumo.dlr.de/docs/Definition_of_Vehicles,_Vehicle_Types,_and_Routes.html) (verified — all five SUMO values confirmed against official docs);
[PTV VISSIM W99 documentation](https://cgi.ptvgroup.com/vision-help/VISSIM_2023_ENG/Content/4_BasisdatenSim/FahrverhaltensparameterFolgeverh_Wied99.htm),
[WisDOT Vissim calibration parameters](https://wisconsindot.gov/dtsdManuals/traffic-ops/manuals-and-standards/teops/16-20att6.3.pdf),
[MDOT SHA VISSIM guidance](https://www.roads.maryland.gov/OPPEN/VISSIM%20Modeling%20Guidance%209-12-2017.pdf)
(W99 defaults CC0=1.50 m, CC1=0.90 s, CC2=4.0 m, CC8=3.5 m/s², CC9=1.5 m/s² confirmed across
three independent sources — high confidence).

**The VISSIM structure is the interesting one:** it separates "how hard a stopped car
launches" (CC8 = 3.5 m/s²) from "how hard it accelerates at speed" (CC9 = 1.5 m/s²) — i.e.
the industry-standard commercial simulator gives standstill launch **more than double** the
cruise acceleration. A single constant `a` (IDM, SUMO) cannot express this shape; real
launch acceleration rises from rest (engine torque; cf. Gipps' `(0.025+v/V)^½` factor) and
decays with speed.

### 2.2 Saturation flow, discharge headway, startup lost time (traffic-engineering ground truth)

- **HCM base saturation flow: 1900 pc/h/ln** (metro areas ≥ 250k population), 1750 pc/h/ln
  smaller communities → saturation headway **1.9–2.06 s**. The classic 1985-HCM value was
  1800 veh/h/ln = 2.0 s headway. (Verified against independent academic sources +
  [Bonneson 1992, TRB Record 1365](https://onlinepubs.trb.org/Onlinepubs/trr/1992/1365/1365-004.pdf) — high confidence.)
- **Startup lost time ≈ 2.0 s/phase** (HCM default; studies range 1.0–2.0 s). Formally
  `L_s = Σ(hₙ − H)` over the first ~4 queue positions. Our 1.33 s is *good* — better than
  typical field values, consistent with our low startup delay but doesn't contradict slow
  sustained discharge. ([Bonneson 1992](https://onlinepubs.trb.org/Onlinepubs/trr/1992/1365/1365-004.pdf), high confidence.)
- **Per-position discharge headway decays**: h₁ ≈ 3.5–4.0 s, dropping steeply through
  positions 2–4, flattening to **1.7–2.1 s by position 5–6** (Bonneson field data, R² 0.88–0.94;
  HCM computes saturation headway from position 5+). Left-turn saturation headways measured
  *lower* than through (1.66–1.97 s, avg 1.75) at the studied interchanges.
- **Why saturation headway > T:** observed discharge headway = steady headway *plus* startup
  dynamics — Bonneson's regression decomposes it into first-driver perception-reaction
  (≈1.03 s), starting response time (≈1.57 s), and a spacing/v_max term; the transient is
  absorbed by the first 4–5 vehicles, after which headway ≈ (s0 + vT-like spacing + L)/v.
  In a *well-behaved* model, sat headway should land near T + (s0+L)/v — for us ≈ 0.95 + 0.7
  ≈ 1.6–1.8 s, not 3.5 s (see §1.4).
- Discharge headway also *shrinks under pressure*: h = 2.41 − 0.026·v (veh/cycle/lane) at one
  site — busy approaches discharge faster. Nice flavour candidate for impatience coupling.
  ([Bonneson 1992](https://onlinepubs.trb.org/Onlinepubs/trr/1992/1365/1365-004.pdf), high confidence.)

---

## 3. Anticipation and reactivity — what makes drivers read as "awake"

### 3.1 What plain IDM lacks

The 25-year IDM review is explicit: IDM has **no reaction time, no estimation error, no
multi-vehicle anticipation**, which "weakens its ability to reproduce realistic driver
behavior and traffic stability, particularly under stop-and-go wave conditions"
([Zhou et al. 2025](https://arxiv.org/html/2506.05909v1), high confidence). Paradoxically the
missing *reaction time* makes IDM feel **less** human-responsive, not more: because IDM has
no anticipation either, it compensates with worst-case caution (§1.2), and because it reacts
only to gap/Δv it cannot respond to "the leader has started moving" until that shows up as
accumulated gap change.

### 3.2 Startup wave: real vs reactive-model

- Real stop-and-go / startup waves propagate upstream at roughly **15–25 km/h** (≈ 4–7 m/s;
  at ~6.5–7 m jam spacing that is **~1.0–1.7 s per vehicle**). Treiber & Kesting cite
  −15 ± 5 km/h for jam fronts; TU Delft's shock-wave chapter computes a 25 km/h start wave in
  a capacity example ([TU Delft ch. 8](https://ocw.tudelft.nl/wp-content/uploads/Chapter-8.-Shock-wave-analysis.pdf))
  — verified band, high confidence on the range, site-dependent within it.
- Empirical per-vehicle start response: first driver crosses ~1.5 s after green with ~0.7 s
  reaction; subsequent drivers launch **~1.0–2.0 s** after their leader (studies scatter:
  start-up delay ≈ 1.1 s + PRT ≈ 1.0 s in one study, British start-up lost time 1.48 s,
  first-driver reactions up to ~2 s at countdown signals) ([arXiv:1408.5498](https://arxiv.org/pdf/1408.5498);
  verification pass across [FHWA TFT ch. 5](https://www.fhwa.dot.gov/publications/research/operations/tft/chap5.pdf) and others — likely, not a single fixed constant).
- Real drivers use **anticipatory cues**: they respond to the leader's brake lights and to
  vehicles further ahead, not just their own gap. Multi-anticipative models
  (Lenz/Wagner/Sollacher 1999; multi-leader IDM extensions) show reacting to 2–3 leaders
  enlarges the stable region, smooths trajectories and damps oscillation amplification;
  ~3 leaders is enough, more adds little
  ([EPJ B 1999](https://epjb.epj.org/articles/epjb/abs/1999/02/b8197/b8197.html), high;
  [multi-anticipative IDM](https://ideas.repec.org/a/wsi/ijmpcx/v21y2010i05ns0129183110015397.html), high;
  [Springer 2021](https://link.springer.com/article/10.1134/S2070048221060107), medium).
- A two-leader ACC controller (leader-2 weighted 0.5) cut stable time-gap from ~1.75 s to
  ~1.05 s while keeping string stability ([PMC 2022](https://pmc.ncbi.nlm.nih.gov/articles/PMC9231564/), medium).

### 3.3 How other models handle standstill departure

- **Gipps (1981):** two-branch min() of a free-acceleration bound and a hard safe-braking
  bound. The acceleration branch contains `2.5·a·τ·(1−v/V)·(0.025+v/V)^½` — the √ factor
  explicitly shapes torque-like rising-then-falling acceleration from rest
  ([Wikipedia: Gipps' model](https://en.wikipedia.org/wiki/Gipps%27_model), high).
- **Krauss (SUMO default):** stochastic Gipps relative; reaction time is emulated by the
  simulation step (tau), `decel` is a hard cap, and `sigma` (default 0.5) randomly degrades
  the chosen speed. Known to err *aggressive* (implausibly short equilibrium gaps) — the
  opposite failure mode from IDM (low confidence on that last claim, search-synthesis only).
- **Wiedemann 74/99 (VISSIM):** psychophysical *action-point* model — drivers only react on
  crossing perception thresholds (AX/BX/SDX/SDV/OPDV…), producing oscillatory,
  human-looking following rather than IDM's smooth convergence; explicit standstill-launch
  knob CC8 (§2.1). Calibration studies: CC0/CC1 dominate capacity (CC1 alone ~18 %);
  CC8 ↑ = aggressive launch profile.

---

## 4. Curve and turn speeds

### 4.1 The standard formula

Traffic engineering point-mass model (AASHTO):

```
v = √( g · R · (e + f) )        e = superelevation, f = side-friction factor
```

with flat urban streets (e≈0) this is exactly **v = √(a_lat · R)**. Design side-friction
values are comfort-based, not physics-limited: f ≈ 0.16 → 0.10 for 40 → 70 mph
(dry-pavement physical friction is 0.5–0.8; drivers *choose* far less). AASHTO's low-speed
urban-street method explicitly assumes drivers accept *more* lateral acceleration at low
speed. ([FHWA Speed Concepts guide](https://highways.dot.gov/safety/speed-management/speed-concepts-informational-guide/chapter-4-engineering-and-technical);
formula and f-vs-speed trend verified against independent design references — high confidence.)

### 4.2 Comfortable lateral acceleration values

| Context | a_lat |
|---|---|
| AASHTO comfort limit, low speed (30 km/h) | 2.1 m/s² |
| AASHTO comfort limit, 40–50 km/h | 1.8 m/s² |
| AASHTO comfort limit, 55–80 km/h | 1.5 m/s² |
| Passenger "comfortable" band (field study) | 0.4–1.3 m/s² |
| Naturalistic urban left turns — **median peak** | ≈ 1.7 m/s² (0.17 g ± 0.08; 86–90 % of drivers stay below ≈ 2.45 m/s²) |
| Naturalistic urban turns — aggressive tail (top ~15–25 %) | 2.5–4 m/s² (instrumented peaks ≈ 3.9 m/s²) |

(Verified: comfort limit *decreases with speed*; median urban-turn peaks are ~1.5–2 m/s²
with only the aggressive tail reaching 2.5–4 m/s² — an earlier draft's "drivers often
tolerate 2–4" overstated the typical case. Sources:
[JS Held naturalistic left-turn study](https://www.jsheld.com/uploads/PDFs/Lateral-and-Tangential-Accelerations-of-Left-Turning-Vehicles-from-Naturalistic-Observations.pdf),
[ScienceDirect comfort standards](https://www.sciencedirect.com/science/article/pii/S0003687022002046),
[mountain-road field study](https://www.redalyc.org/journal/430/43067842009/html/).)

### 4.3 Game precedent and the known pitfall

Open-city racing AI (Midtown Madness lineage) uses **exactly this formula** for ambient
traffic and AI cornering — `V = √(u·g·R)` — and then "all that the [AI] has to do is slow
down to the correct speed *before entering the turn*"
([Game Developer: "AI Madness: Using AI to Bring Open-City Racing to Life"](https://www.gamedeveloper.com/programming/ai-madness-using-ai-to-bring-open-city-racing-to-life), high confidence, verbatim).
The pitfall is the second half: curve speed must feed a **braking-distance lookahead**
(decelerate at ≤ b in advance), otherwise you get speed discontinuities at curve entry.

### 4.4 What this means for our fixed 9–10 m/s turns *(our derivation — speculation)*

v = √(a_lat·R) at a_lat = 2.0 m/s²: R=10 m → 4.5 m/s; R=15 m → 5.5 m/s; R=30 m → 7.7 m/s;
R=50 m → 10 m/s. Our fixed 9–10 m/s corresponds to R ≈ 40–50 m — far larger than typical
urban corner radii (10–15 m). So our turns are likely **too fast through tight corners**
(reads as arcade-ish/drifty) and **too slow on sweeping connectors** (reads as sluggish).
Geometry-based speed adds organic variety for free since we already have curve radii.

---

## 5. Gap acceptance

### 5.1 HCM ground truth (TWSC intersections, base values, passenger cars)

| Movement | Critical gap t_c (2-lane / 4-lane major) | Follow-up time t_f |
|---|---|---|
| Left turn **from major** (across opposing) | **4.1 s / 4.1 s** | 2.2 s |
| Right turn from minor | 6.2 s / 6.9 s | 3.3 s |
| Through from minor | 6.5 s / 6.5 s | 4.0 s |
| Left turn from minor | 7.1 s / 7.5 s | 3.5 s |

([HCM Exhibit 17-2 via IIT-B lecture notes](https://www.civil.iitb.ac.in/tvm/nptel/564_UnCotrl/web/web.html);
every value independently confirmed against [Bentley CUBE's HCM reproduction](https://docs.bentley.com/LiveContent/web/CUBE%207-v1/en/GUID-3BE70300-5C19-411E-B811-29E5A57819A5.html) — verified, high confidence.
Roundabout entry critical headways cluster ~3.5–4.6 s; follow-up ≈ 0.6·t_c — low-medium confidence.)

Two distinct quantities matter: **critical gap** (min gap a waiting driver accepts) and
**follow-up time** (headway between successive minor-stream vehicles using the *same* gap —
this is what governs discharge *through* a yielding movement once a big gap appears).

### 5.2 Impatience models

- Literature consensus (verified across independent sources — Mahmassani & Sheffi 1981,
  Polus et al. 2003, TRB observational studies): critical gap **decreases with waiting
  time**, best fit by a **logistic / S-shaped decay** between a max (patient) and min
  (floor) critical gap
  ([ASCE JTE 129(5) 2003](https://ascelibrary.org/doi/10.1061/%28ASCE%290733-947X%282003%29129%3A5%28504%29);
  [Tupper et al., TRB 2011](https://onlinepubs.trb.org/onlinepubs/conferences/2011/RSS/1/Tupper,S.pdf) — high confidence on direction and shape).
  Reported magnitude ≈ **0.5–1.0 s reduction** (one roundabout study: 0.9 s) — likely, site-dependent.
  Long-waiting drivers eventually execute *forcing* maneuvers (accept gaps that make the
  major-stream vehicle brake).
- **SUMO's implementation** (verbatim formula, high confidence):
  `impatience = MAX(0, MIN(1.0, baseImpatience + waitingTime / timeToMaxImpatience))`,
  default `timeToMaxImpatience = 180 s`; impatience 1.0 = "accept any gap that is still
  collision-free, even if it forces others to brake"
  ([SUMO Safety docs](https://sumo.dlr.de/docs/Simulation/Safety.html)). SUMO also has
  `jmTimegapMinor` (default 1 s) as the floor gap when crossing before a prioritized vehicle.

### 5.3 Comparison to ours *(assessment — speculation)*

Our 2.8 → 2.2 s is **far more aggressive than HCM reality** (4.1–7.5 s) — closer to SUMO's
collision-floor than to a critical gap. For game feel that's a legitimate choice (real
critical gaps would read as timid), but note ours is a *single* value: HCM's structure says
the differentiator drivers actually exhibit is **per-movement** (major-left ≈ 4.1 s vs
minor-left ≈ 7.5 s, ratio ~1.8×) plus a **separate follow-up time** ≈ 0.5–0.6× critical gap
that lets a platoon of yielders stream through one large gap — that streaming is a big
"assertive traffic" visual. Our linear-ish 2.8→2.2 impatience is directionally right;
literature shape is logistic with a floor, over ~1–3 min of waiting.

---

## 6. Game-feel evidence (thinner sourcing, flagged)

- Colossal Order explicitly traded realism for feel/playability in Cities: Skylines:
  mid-segment lane changing was tried and reverted ("it actually made everything worse"),
  and stuck vehicles teleport because otherwise "the reaction time required to catch the
  traffic problems… would simply be too short"
  ([Game Developer: How traffic works in Cities: Skylines](https://www.gamedeveloper.com/design/how-traffic-works-in-cities-skylines);
  [Deep Dive: traffic systems](https://www.gamedeveloper.com/design/game-design-deep-dive-traffic-systems-in-i-cities-skylines-i-), high confidence).
- CS2 markets its traffic AI in feel language: agents "sneak through an intersection at the
  last minute" and fill lanes evenly ([Paradox feature page](https://www.paradoxinteractive.com/games/cities-skylines-ii/features/traffic-ai), medium — marketing copy).
- TM:PE modders hit the inverse problem — a fix made yield junctions *too* smooth, and they
  reintroduced per-control speed factors (yield ≈ 60 % of limit, stop ≈ 30 %, with a floor)
  to restore behavioral differentiation ([TMPE issue #686](https://github.com/CitiesSkylinesMods/TMPE/issues/686), high).
- Treiber himself detunes IDM away from realism for effect: traffic-simulation.de ships
  a = 0.3 m/s² (vs "realistic 0.8–2.5") specifically "to enhance the formation of stop-and-go
  traffic" — parameter-as-experience-design, from the model's author
  ([traffic-simulation.de](https://traffic-simulation.de/info/info_IDM.html), high).
- Wiedemann-style oscillatory following (drivers drift around the desired gap between
  perception thresholds) is considered *more* human-looking than IDM's asymptotically smooth
  convergence — imperfection reads as life (medium confidence, multiple secondary sources).
- No GDC talk was found that addresses car-following tuning for perceived assertiveness
  directly; racing-AI sources (GT Sophy's 10 Hz "human reaction time" loop, skill knobs that
  modulate braking-early/accelerating-out-less) are analogies, not precedents.

---

## 7. Concrete candidate changes for our IDM — ranked by expected feel-impact

1. **Adopt IDM+ (min() instead of superposition).** *(top impact, ~1-line change)*
   `accel = a · min(1 − (v/v0)^δ, 1 − (s*/s)²)`. Directly attacks slow discharge and
   gap inflation; equilibrium gap becomes exactly s0+vT so saturation headway should land
   near T + (s0+L)/v ≈ 1.6–1.8 s for our params. Literature-proven (Schakel 2010; Treiber's
   own IIDM). Regression risk: slightly harsher transitions near v0 — visually negligible.

2. **Leader-start anticipation at standstill.** When stopped and the leader is accelerating
   (or has begun moving), begin launching after a short per-driver start delay
   (~0.5–1.0 s; empirical per-vehicle values are 1.0–2.0 s — going slightly faster than
   reality is a deliberate feel choice) instead of waiting for the gap to physically open
   past s0. Cheapest robust form: if
   `v == 0 && leader.v > ε` (or leader accel > 0), apply launch acceleration ramp regardless
   of `(s0/s)²` (safety still enforced by the braking term as speed builds). This reproduces
   the empirical ~1–1.3 s/vehicle startup wave and is the single biggest "drivers are awake"
   cue. *(Implementation form is our design; the empirical target is well-sourced.)*

3. **CAH/"coolness" blend to stop over-braking (assertive approach behavior).**
   Kesting–Treiber–Helbing ACC model with c ≈ 0.99: when plain IDM wants to brake much harder
   than the constant-acceleration extrapolation warrants, relax toward CAH. Fixes skittish
   braking on merges, junction exits, and mid-queue compressions — cars approach closer and
   more confidently without safety loss. Moderate implementation cost (needs leader
   acceleration, which we have). Consider also trimming b from 2.8 → ~2.0 m/s².

4. **Speed-dependent launch acceleration (VISSIM CC8/CC9 shape).** Replace constant `a` with
   a(v): ~3.0–3.5 m/s² near standstill decaying to ~1.5–2.0 m/s² at 80 km/h (linear or
   Gipps-style `(0.025+v/V)^½` shape). Punchy launches without highway-speed rocketing.
   If jerk at t=0 looks robotic, add the EIDM tanh ramp over t_amax ≈ 0.5–1 s.

5. **Geometry-based turn speeds: v_turn = clamp(√(a_lat·R), v_min, v_limit)** with
   a_lat ≈ 2.0–2.5 m/s² (game-feel end of the comfort range) and a braking-distance lookahead
   to decelerate before curve entry (we already have deceleration planning for junctions).
   Slower tight corners + faster sweepers = organic variety; matches both AASHTO and the
   shipped-game precedent. Suggested v_min ≈ 3–4 m/s so tight corners don't crawl.

6. **Structured gap acceptance:** keep our aggressive absolute scale but (a) differentiate
   by movement using HCM *ratios* (minor-left ≈ 1.8× major-left; right-turn between),
   (b) add a separate follow-up time ≈ 0.5–0.6× critical gap so several waiting cars stream
   through one large gap, (c) reshape impatience as logistic decay to a floor over ~60–180 s
   (SUMO-style `waiting/timeToMax`), optionally with a rare "forcing" maneuver at max
   impatience that makes the conflicting car brake — a strong assertiveness read.

7. **Driver heterogeneity + action-point noise.** Per-driver multipliers on T, a, v0
   (±10–20 %) and Wiedemann-flavoured oscillation (only correct speed when the error crosses
   a perception threshold). Makes following look human rather than servo-controlled; also the
   literature's fix for IDM's wrong (convex) oscillation growth. Do this *after* 1–4 — noise
   on top of a sluggish base model just looks like sluggish noise.

**Cross-cutting first step (before any of the above):** instrument per-queue-position
discharge headways h₁…h₉ at a test signal and compare with Bonneson's decay curve
(h₁ ≈ 3.5–4 s → ~2 s by position 5). §1.4 suggests our 3.52 s saturation headway exceeds
what our parameter set should produce even in plain IDM — if positions 6+ are still > 3 s,
look for an implementation-level cause (junction gap checks, turn caps, measurement) in
parallel with the model changes.

---

## Source index (primary/most load-bearing)

- Treiber's IDM reference page: https://traffic-simulation.de/info/info_IDM.html
- Kesting, Treiber, Helbing 2010 (ACC/CAH, "coolness factor"): https://arxiv.org/pdf/0912.3613
- Schakel, van Arem, Netten 2010 (IDM+): https://ieeexplore.ieee.org/document/5625133
- Salles, Kaufmann, Reuss 2020 (EIDM, SUMO, drone-measured drive-offs): https://sumo.dlr.de/2020/SUMO2020_paper_28.pdf
- Zhou et al. 2025, "Twenty-Five Years of the Intelligent Driver Model": https://arxiv.org/pdf/2506.05909
- Albeaik et al. 2022, IDM limitations/well-posedness: https://arxiv.org/pdf/2104.02583
- Bonneson 1992, discharge headway & startup lost time field study: https://onlinepubs.trb.org/Onlinepubs/trr/1992/1365/1365-004.pdf
- Kovács CARLA/IDM signalized-intersection companion paper: https://gradus.kefo.hu/archive/2021-3/2021_3_CSC_001_Kovacs.pdf
- HCM TWSC critical gaps (Exhibit 17-2 reproduction): https://www.civil.iitb.ac.in/tvm/nptel/564_UnCotrl/web/web.html
- SUMO safety/impatience docs: https://sumo.dlr.de/docs/Simulation/Safety.html
- SUMO vType defaults: https://github.com/eclipse-sumo/sumo/blob/main/docs/web/docs/Vehicle_Type_Parameter_Defaults.md
- PTV VISSIM Wiedemann 99 parameters: https://cgi.ptvgroup.com/vision-help/VISSIM_2023_ENG/Content/4_BasisdatenSim/FahrverhaltensparameterFolgeverh_Wied99.htm
- Game Developer, "AI Madness" (open-city racing AI cornering): https://www.gamedeveloper.com/programming/ai-madness-using-ai-to-bring-open-city-racing-to-life
- Game Developer, Cities: Skylines traffic deep-dives: https://www.gamedeveloper.com/design/how-traffic-works-in-cities-skylines
- ASCE, critical gap as a function of waiting time: https://ascelibrary.org/doi/10.1061/%28ASCE%290733-947X%282003%29129%3A5%28504%29
- Gipps model: https://en.wikipedia.org/wiki/Gipps%27_model
- Multi-anticipative car following (Lenz/Wagner/Sollacher): https://epjb.epj.org/articles/epjb/abs/1999/02/b8197/b8197.html
