# Elevated Ghost Feedback & CS2 Gradient Caps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the elevated-road ghost show real height feedback (pillars, ground footprint, elevation labels) and raise per-type gradient caps to CS2-style game-feel values.

**Architecture:** Domain change is data-only (`RoadCatalog.MaxGradient` values). Game change extracts `StructureView`'s edge mesher into a curve-based static that `GhostView` reuses with ghost materials, plus a flattened "shadow" strip and pooled `Label3D` elevation badges — all driven from the existing `ValidatedPlacement` dirty flag. No new domain surface, no session state.

**Tech Stack:** Godot 4.7 mono/C#, xUnit (domain tests, net10.0), screenshot harness (`CITYBUILDER_SHOTS`).

**Spec:** `docs/superpowers/specs/2026-07-20-elevated-ghost-feedback-design.md`

## Global Constraints

- `src/Domain` never references Godot (golden rule 1).
- New gradient caps: Street/OneWay **0.20**, TwoLane/Asymmetric **0.15**, FourLane/Avenue **0.12** — exactly these values.
- Elevation visual threshold everywhere in this feature: **Y > 0.5 m**.
- Committed bridge rendering must be byte-identical after the extraction (pure refactor).
- Repo hygiene: never `git add -A`; add files explicitly.
- Every task ends green: `dotnet test` then `dotnet build citybuilder.sln`.

---

### Task 1: CS2 gradient caps in RoadCatalog

**Files:**
- Modify: `src/Domain/Catalog/RoadType.cs` (the `RoadCatalog` static, lines ~60–133)
- Test: `tests/Domain.Tests/Network/ElevationValidationTests.cs`
- Test: `tests/Domain.Tests/Network/RetypeTests.cs` (test at ~line 139)
- Test: `tests/Domain.Tests/Network/ElevationNetworkTests.cs` (test at ~line 66)

**Interfaces:**
- Consumes: existing `RoadType.MaxGradient` (float, last positional param of the record).
- Produces: new cap values every later consumer (validate/retype/roundabout/invariants/fuzz) reads automatically. No signature changes.

- [ ] **Step 1: Move the boundary tests to the new caps (failing first)**

In `tests/Domain.Tests/Network/ElevationValidationTests.cs`:

```csharp
    [Fact]
    public void SteepRampIsTooSteep()
    {
        var n = Net.New();
        var v = n.Validate(One(Ramp(new(0, 0, 0), new(100, 18, 0)))); // 18% > TwoLane 15%
        Assert.Contains(PlacementError.TooSteep, v.Errors);
    }

    [Fact]
    public void GradientLimitIsPerType()
    {
        var n = Net.New();
        // 17% is TooSteep for TwoLane (15%) but fine for Street (20%)
        Assert.Contains(PlacementError.TooSteep,
            n.Validate(One(Ramp(new(0, 0, 0), new(100, 17, 0)), RoadCatalog.TwoLane.Id)).Errors);
        Assert.True(n.Validate(One(Ramp(new(0, 0, 0), new(100, 17, 0)), RoadCatalog.Street.Id)).IsValid);
    }
```

(`GentleRampIsValid` at 6% stays untouched — still legal.)

In `tests/Domain.Tests/Network/RetypeTests.cs`, `RetypeRefusesWhenTheExistingRampExceedsTheNewTypesGradient`: change the ramp from 8 m rise to 13 m and update the comment percentages:

```csharp
        // M8 fuzz find (303@3987): a 13% ramp legal on TwoLane must not retype onto a
        // 12% type — gradient is the fourth member of the retype floor checks
        var n = Net.New();
        var ramp = CityBuilder.Domain.Geometry.Bezier3.Line(
            new Vector3(0, 0, 0), new Vector3(100, 13, 0)); // 13%
```

and the assert comments: `// 12% max` on the FourLane line, `// 20% max: fine` on the Street line.

In `tests/Domain.Tests/Network/ElevationNetworkTests.cs`, `ConvertingAJunctionWithSteepRampLegsIsRefusedNotCorrupted`: legs move from the old full-cap 4.8 m/60 m (8%) to the new full-cap 9 m/60 m (15%); update the comment to say 15%:

```csharp
        // TwoLane legs at their full 15%: descending from the cut height to the ring
        // plane over the trimmed remainder needs >15% — refuse, never commit corrupt
        var n = Net.New();
        Net.Commit(n, One(Ramp(new(-60, 9f, 0), new(0, 0, 0))));
        Net.Commit(n, One(Ramp(new(60, 9f, 0), new(0, 0, 0))));
        Net.Commit(n, One(Ramp(new(0, 9f, -60), new(0, 0, 0))));
```

- [ ] **Step 2: Run the touched tests, verify they fail against the old caps**

Run: `dotnet test --filter "FullyQualifiedName~ElevationValidationTests|FullyQualifiedName~RetypeTests|FullyQualifiedName~ElevationNetworkTests"`
Expected: FAIL — `SteepRampIsTooSteep` still passes (18% > 8%) but `GradientLimitIsPerType` fails (17% > 10% Street cap → not valid), retype test fails (13% ramp won't commit on TwoLane 8%), roundabout test fails (9 m/60 m legs won't commit on TwoLane 8%).

- [ ] **Step 3: Change the caps in RoadCatalog**

In `src/Domain/Catalog/RoadType.cs`, change only the final `MaxGradient` argument of each type:

| Type | line ~ | old literal | new literal |
|---|---|---|---|
| TwoLane | 67 | `80f, 20f, 0.08f);` | `80f, 20f, 0.15f);` |
| FourLane | 78 | `100f, 35f, 0.06f);` | `100f, 35f, 0.12f);` |
| Street | 90 | `50f, 10f, 0.10f);` | `50f, 10f, 0.20f);` |
| Avenue | 106 | `60f, 25f, 0.06f);` | `60f, 25f, 0.12f);` |
| OneWay | 119 | `50f, 10f, 0.10f);` | `50f, 10f, 0.20f);` |
| Asymmetric | 131 | `60f, 20f, 0.08f);` | `60f, 20f, 0.15f);` |

- [ ] **Step 4: Full test run**

Run: `dotnet test`
Expected: all green (305-ish, no count change). If any *other* test fails it was silently pinned to old caps — move its boundary the same way (same shape, new numbers) and note it in the commit message.

- [ ] **Step 5: Fuzz spot run (caps only loosened, must stay green)**

Run: `CITYBUILDER_FUZZ_ACTIONS=10000 dotnet test --filter "FullyQualifiedName~FuzzSuiteTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Domain/Catalog/RoadType.cs tests/Domain.Tests/Network/ElevationValidationTests.cs tests/Domain.Tests/Network/RetypeTests.cs tests/Domain.Tests/Network/ElevationNetworkTests.cs
git commit -m "feat(domain): CS2-style gradient caps (20/15/12%) — playability over realism"
```

---

### Task 2: Extract curve-based structure mesher

**Files:**
- Modify: `src/Game/StructureView.cs` (`BuildStructures`, lines ~79–153)

**Interfaces:**
- Consumes: `Bezier3`, `ArcLengthTable` (`new ArcLengthTable(in Bezier3, int samples = 128)`, `.TotalLength`, `.TAtDistance(float)`), `RoadEdge.ArcLength`/`.Curve`/`.Type`.
- Produces: `public static ArrayMesh? BuildStructures(in Bezier3 curve, ArcLengthTable arc, float width)` on `StructureView` — Task 3 calls exactly this. Returns null when the curve never leaves the ground. Surfaces keep baked `Materials.Earth`/`Materials.Concrete`.

- [ ] **Step 1: Refactor**

Replace the private `BuildStructures(RoadEdge edge)` with a public curve-based static plus a delegating private overload. The body is the existing one with `edge.ArcLength` → `arc`, `edge.Curve` → `curve`, and the width lookup hoisted to the caller:

```csharp
    private static ArrayMesh? BuildStructures(RoadEdge edge)
        => BuildStructures(edge.Curve, edge.ArcLength, RoadCatalog.Get(edge.Type).Width);

    /// <summary>One ArrayMesh: surface 0 = earth embankment skirts, surface 1 =
    /// concrete fascia + pillars. Null when the curve never leaves the ground.
    /// Public and curve-based so GhostView previews the exact structures a commit
    /// would produce (same thresholds, same code).</summary>
    public static ArrayMesh? BuildStructures(in Bezier3 curve, ArcLengthTable arc, float width)
    {
        float len = arc.TotalLength;
        int n = Mathf.Max(2, (int)(len / SampleStep));
        var pts = new Vector3[n + 1];
        var side = new Vector3[n + 1];
        bool anyElevated = false;
        for (int i = 0; i <= n; i++)
        {
            float t = arc.TAtDistance(len * i / n);
            var p = curve.Point(t);
            var tan = curve.Tangent(t);
            pts[i] = p.ToGodot();
            var s = new Vector3(tan.Z, 0, -tan.X);
            side[i] = s.LengthSquared() > 1e-9f ? s.Normalized() : Vector3.Right;
            if (p.Y > 0.05f)
                anyElevated = true;
        }
        if (!anyElevated)
            return null;

        float half = width / 2f;
        // ... rest of the existing body, verbatim from here on ...
```

Everything from `var earth = new SurfaceTool();` down is untouched. Add `using CityBuilder.Domain.Geometry;` if not already imported (it is — `GeoConstants` is used). Note the old body read `RoadCatalog.Get(edge.Type).Width / 2f` — that lookup moves into the delegating overload.

- [ ] **Step 2: Verify pure refactor**

Run: `dotnet test && dotnet build citybuilder.sln`
Expected: tests green, build clean (0 warnings introduced).

- [ ] **Step 3: Commit**

```bash
git add src/Game/StructureView.cs
git commit -m "refactor(game): curve-based StructureView.BuildStructures for ghost reuse"
```

---

### Task 3: Ghost structures + ground footprint + elevation labels

**Files:**
- Modify: `src/Game/Materials.cs` (add `GhostShadow`)
- Modify: `src/Game/GhostView.cs`

**Interfaces:**
- Consumes: `StructureView.BuildStructures(in Bezier3, ArcLengthTable, float)` from Task 2; `MeshBuilders.BuildGhostStrip(in Bezier3, float)`; `ArcLengthTable`.
- Produces: no API change — `GhostView.Show(...)` signature is untouched; all new visuals derive from `placement.Proposal.Curves`.

- [ ] **Step 1: Add the shadow material**

In `src/Game/Materials.cs`, after `GhostInvalid`:

```csharp
    /// <summary>Ground projection under an elevated ghost — a shadow, not a road:
    /// dark, faint, and never occluding (no depth write).</summary>
    public static readonly StandardMaterial3D GhostShadow = new()
    {
        AlbedoColor = new Color(0.05f, 0.08f, 0.15f, 0.25f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        DisableReceiveShadows = true,
        RenderPriority = -1,
    };
```

- [ ] **Step 2: New pools and helpers in GhostView**

Fields, next to `_strips`/`_handles`:

```csharp
    private readonly List<MeshInstance3D> _structures = new();
    private readonly List<MeshInstance3D> _shadows = new();
    private readonly List<Label3D> _elevLabels = new();
```

`Clear()` gains:

```csharp
        HideFrom(_structures, 0);
        HideFrom(_shadows, 0);
        HideLabelsFrom(_elevLabels, 0);
```

with a `Label3D` twin of `HideFrom` (the existing one is typed `List<MeshInstance3D>`):

```csharp
    private static void HideLabelsFrom(List<Label3D> pool, int from)
    {
        for (int i = from; i < pool.Count; i++)
            pool[i].Visible = false;
    }

    private Label3D PooledLabel(ref int used)
    {
        if (used == _elevLabels.Count)
        {
            var l = new Label3D
            {
                FontSize = 56,
                PixelSize = 0.05f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = new Color(0.55f, 0.85f, 1f),
                OutlineSize = 12,
            };
            AddChild(l);
            _elevLabels.Add(l);
        }
        var node = _elevLabels[used++];
        node.Visible = true;
        return node;
    }
```

- [ ] **Step 3: Build structures, shadows, and labels inside the placement-changed branch**

In `Show(...)`, extend the existing `if (!ReferenceEquals(placement, _lastPlacement))` block (which currently rebuilds `_strips`) so the same dirty flag drives everything mesh-shaped:

```csharp
            if (!ReferenceEquals(placement, _lastPlacement))
            {
                int used = 0;
                int usedStructures = 0, usedShadows = 0, usedLabels = 0;
                var material = placement.IsValid ? Materials.GhostValid : Materials.GhostInvalid;
                float width = RoadCatalog.Get(placement.Proposal.Type).Width;
                var labelled = new List<Vector3>();
                foreach (var pc in placement.Proposal.Curves)
                {
                    var mesh = MeshBuilders.BuildGhostStrip(pc.Curve, width);
                    if (mesh is null)
                        continue;
                    var inst = Pooled(_strips, ref used);
                    inst.Mesh = mesh;
                    inst.MaterialOverride = material;

                    bool elevated = pc.Curve.P0.Y > 0.5f || pc.Curve.P1.Y > 0.5f
                        || pc.Curve.P2.Y > 0.5f || pc.Curve.P3.Y > 0.5f;
                    if (elevated)
                    {
                        // the exact structures a commit would produce (same mesher)
                        var arc = new CityBuilder.Domain.Geometry.ArcLengthTable(pc.Curve);
                        if (StructureView.BuildStructures(pc.Curve, arc, width) is { } structMesh)
                        {
                            var s = Pooled(_structures, ref usedStructures);
                            s.Mesh = structMesh;
                            s.MaterialOverride = material;
                        }

                        // ground footprint: the curve flattened to Y=0 (a cubic's Y
                        // is bounded by its control net, so this is the exact XZ shadow)
                        var flat = new CityBuilder.Domain.Geometry.Bezier3(
                            pc.Curve.P0 with { Y = 0 }, pc.Curve.P1 with { Y = 0 },
                            pc.Curve.P2 with { Y = 0 }, pc.Curve.P3 with { Y = 0 });
                        if (MeshBuilders.BuildGhostStrip(flat, width) is { } shadowMesh)
                        {
                            var sh = Pooled(_shadows, ref usedShadows);
                            sh.Mesh = shadowMesh;
                            sh.MaterialOverride = Materials.GhostShadow;
                        }

                        AddElevationLabel(pc.Curve.P0, labelled, ref usedLabels);
                        AddElevationLabel(pc.Curve.P3, labelled, ref usedLabels);
                    }
                }
                HideFrom(_strips, used);
                HideFrom(_structures, usedStructures);
                HideFrom(_shadows, usedShadows);
                HideLabelsFrom(_elevLabels, usedLabels);
            }
```

(The `float width` lookup moves out of the per-curve loop — it was per-iteration before for no reason.)

And the helper — one badge per unique elevated endpoint, chained curves share joints:

```csharp
    /// <summary>"⬆ N m" badge over an elevated ghost endpoint. Dedupes shared joints
    /// of chained curves by proximity (1 m).</summary>
    private void AddElevationLabel(System.Numerics.Vector3 p, List<Vector3> labelled, ref int used)
    {
        if (p.Y <= 0.5f)
            return;
        var pos = p.ToGodot();
        foreach (var seen in labelled)
            if (pos.DistanceTo(seen) < 1f)
                return;
        labelled.Add(pos);
        var l = PooledLabel(ref used);
        l.Position = pos + Vector3.Up * 4f;
        l.Text = $"⬆ {p.Y:0} m";
    }
```

Also: the `else` branch of `if (placement is not null)` currently does `HideFrom(_strips, 0);` — it gains the same three hide calls as `Clear()` (structures, shadows, labels), so releasing a draft doesn't leave floating badges.

- [ ] **Step 4: Build**

Run: `dotnet test && dotnet build citybuilder.sln`
Expected: green / clean. (Game layer has no headless unit tests; visual proof is Task 4.)

- [ ] **Step 5: Commit**

```bash
git add src/Game/Materials.cs src/Game/GhostView.cs
git commit -m "feat(game): elevated ghost shows real structures, ground footprint, elevation badges"
```

---

### Task 4: Screenshot scenario + full verification

**Files:**
- Modify: `src/Game/VisualShots.cs` (append a scenario after `SnapGallery()` in the scenario iterator, ~line 651, following the `SnapGallery` Extra pattern at ~654–720)

**Interfaces:**
- Consumes: `DraftSession` (`SetMode(DraftMode.Straight)`, `CurrentElevation` setter, `Click(pos, radius)`, `PointerMoved(pos, radius)`, `.Ghost`, `.LastSnap`), `GhostView.Show`, harness `Scenario`/`Shot`/`Commit`/`Straight` helpers already used in the file.
- Produces: two new shots `elevated_ghost_valid` and `elevated_ghost_steep` in the harness output.

- [ ] **Step 1: Add the scenario**

After the `yield return SnapGallery();` line add `yield return ElevatedGhostGallery();`, and below `SnapGallery()`'s method add:

```csharp
    private static Scenario ElevatedGhostGallery()
    {
        return new Scenario("elevated_ghost", n =>
        {
            // a ground road under station 1 so the footprint shadow reads against it
            Commit(n, Straight(new(-40, 0, 20), new(40, 0, 20)));
        }, new[]
        {
            new Shot("elevated_ghost_valid", new NVec(0, 6, 20), 70, -55f, 18f),
            new Shot("elevated_ghost_steep", new NVec(200, 6, 20), 70, -55f, 18f),
        }, Extra: n =>
        {
            var root = new Node3D { Name = "elevated_ghost" };

            void Station(NVec from, NVec to, float elevation)
            {
                var session = new DraftSession(n, new SnapEngine(n));
                session.SetMode(DraftMode.Straight);
                var ghost = new GhostView();
                root.AddChild(ghost);
                ghost.Ready += () =>
                {
                    session.Click(from, 6f);
                    session.CurrentElevation = elevation;
                    session.PointerMoved(to, 6f);
                    ghost.Show(session.Ghost, session.LastSnap);
                };
            }

            // 1: +12 m over 120 m (10%, legal on TwoLane 15%) crossing the ground road:
            //    pillars + fascia, shadow footprint, ⬆ 12 m badge, blue
            Station(new NVec(-60, 0, 60), new NVec(-60 + 120, 0, -30), 12f);
            // 2: +12 m over ~60 m (20% > 15%): same visuals but red (TooSteep)
            Station(new NVec(170, 0, 60), new NVec(170 + 55, 0, 40), 12f);

            return root;
        });
    }
```

**Adaptation note (allowed, expected):** match the real `Scenario`/`Shot`/`Extra` constructor shapes and the exact way `SnapGallery` returns/attaches its root node (read the surrounding file first; e.g. `Extra` may be `Action<RoadNetwork>` with `root` added via an outer capture — copy `SnapGallery`'s exact mechanics). If `DraftSession.CurrentElevation` must be set before `Click` to elevate the anchor, set it before. The two stations must land one **valid elevated** ghost and one **TooSteep** ghost; tune run lengths with the caps from Task 1 if needed.

- [ ] **Step 2: Build, then run the screenshot harness**

Run: `dotnet build citybuilder.sln`
Run: `CITYBUILDER_SHOTS=tests/visual/shots godot .` (needs a window; use the exe path from CLAUDE.md if `godot` is not on PATH)
Expected: `tests/visual/shots/elevated_ghost_valid.png` and `elevated_ghost_steep.png` produced.

- [ ] **Step 3: Read both screenshots**

Open both PNGs. Confirm: (a) pillars/fascia under the elevated deck, ghost-blue in the valid shot, ghost-red in the steep shot; (b) dark footprint strip on the ground under the deck; (c) a `⬆ 12 m` badge at the elevated tip; (d) no z-fighting garbage at the ground end. Fix and re-shoot until all four hold.

- [ ] **Step 4: Full gates**

Run: `dotnet test`
Run: `dotnet build citybuilder.sln`
Run: `CITYBUILDER_SMOKE=1 godot --headless .`
Expected: tests green, build clean, `SMOKE OK`.

- [ ] **Step 5: Commit**

```bash
git add src/Game/VisualShots.cs
git commit -m "test(visual): elevated ghost gallery — structures, footprint, badges, steep-red"
```

---

### Task 5: Docs drift

**Files:**
- Modify: `docs/roadmap.md` (M8/post-M8 notes: gradient caps changed, ghost feedback shipped)
- Modify: manual chapter 10 (elevation) — the gradient table and ghost-feedback description, wherever `docs/` keeps it (grep for the old `10%`/`8%`/`6%` caps)

**Interfaces:** none — prose only.

- [ ] **Step 1: Update the numbers and the ghost description**

Grep: `grep -rn "10%\|8%\|6%\|MaxGradient" docs/ --include="*.md" -l` and update every live document (roadmap, manual ch. 10, conventions if it lists caps) to the new 20/15/12% values and the new ghost visuals (structures preview, footprint shadow, elevation badges). Do NOT touch historical records: specs, health reports, and KPI baselines are point-in-time documents.

- [ ] **Step 2: Commit**

```bash
git add docs/roadmap.md <manual files touched>
git commit -m "docs: gradient caps 20/15/12% + elevated ghost feedback"
```

---

## Self-Review

- Spec coverage: caps → Task 1; extraction → Task 2; structures/footprint/labels → Task 3; verification incl. screenshots + fuzz → Tasks 1 & 4; docs → Task 5. Spec's "smoke" gate → Task 4 Step 4. ✔
- No placeholders; every code step shows the code. The one deliberate degree of freedom (harness constructor shapes in Task 4) is called out with the invariant the implementer must preserve.
- Types consistent: `BuildStructures(in Bezier3, ArcLengthTable, float)` defined in Task 2 = consumed in Task 3; `HideLabelsFrom`/`PooledLabel` defined and used only in Task 3.
