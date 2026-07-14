# citybuilder

A Cities: Skylines 2–inspired city builder in Godot 4.6 (mono/C#), built milestone by
milestone — **it's a marathon, not a sprint**. The road network and traffic systems are
the foundation everything else grows on.

## Topic guides (read the one matching your task)

- [docs/architecture.md](docs/architecture.md) — domain/game split, modules, data flow
- [docs/conventions.md](docs/conventions.md) — units, axes, traffic frame, key constants
- [docs/verification.md](docs/verification.md) — tests, smoke, screenshot/motion harnesses, debug methodology
- [docs/gotchas.md](docs/gotchas.md) — hard-won Godot + domain pitfalls; read before touching rendering or the sim
- [docs/roadmap.md](docs/roadmap.md) — what's done, what's next on the way to CS2 gameplay

## Golden rules

1. **Domain stays pure.** `src/Domain` (net8.0, System.Numerics) never references Godot.
   All game state lives there; `src/Game` only renders and forwards input.
2. **Every change is verified before it's called done**: `dotnet test`, then
   `dotnet build citybuilder.sln`, then the harness matching the change
   (see [docs/verification.md](docs/verification.md)). Visual changes are verified by
   reading the produced screenshots, motion changes by continuity tests + trail composites.
3. **Commit at every milestone / green step.** Specs and plans live in
   `docs/superpowers/specs|plans` and are written before implementing.
4. Prefer regression tests that assert *invariants* (no collision, no teleport, symmetric
   geometry) over example-based asserts — they keep catching bugs later.

## Quick commands

```bash
dotnet test                                   # 190 xUnit domain tests, headless
dotnet build citybuilder.sln                  # domain + game + tests
CITYBUILDER_SMOKE=1 godot --headless .        # scripted end-to-end, prints SMOKE OK
CITYBUILDER_SHOTS=tests/visual/shots godot .  # screenshot harness (needs a window)
CITYBUILDER_UITEST=/tmp/ui.png godot .        # scripted UI flow + full-UI screenshot
```

`godot` is 4.6.2 mono on PATH; dotnet 10 SDK (tests target net10.0, game/domain net8.0).
