using System.Text;
using System.Text.Json;
using Xunit;

namespace CityBuilder.Domain.Tests.Kpi;

/// <summary>KPI harness: runs every deterministic scenario in
/// <see cref="KpiScenarios"/>, merges their metrics, and writes the per-milestone
/// health report under docs/health/{Milestone}.md. First run (no baseline on disk)
/// bootstraps the baseline and passes; every later run asserts non-perf, non-diag
/// metrics stay within a ±25% band of the baseline and perf metrics stay under their
/// absolute ceilings, then refreshes kpi-latest.json + the milestone report. Perf
/// ceilings are never banded — they're hard budgets regardless of history. Metrics
/// prefixed "diag." are diagnostics-only: reported for visibility but never banded
/// and never persisted into kpi-baseline.json.</summary>
public class KpiSuiteTests
{
    /// <summary>Controls the report filename: docs/health/{Milestone}.md. Bump this
    /// when a milestone's KPI pass starts; earlier milestone reports stay in git
    /// history under their own filename (never deleted).</summary>
    private const string Milestone = "M8.5";

    private const float BandPct = 0.25f;
    private const float ValidateCeilingMs = 150f;
    // grade-separated draw over the whole grid: the M8.5 Y-band prefilters skip every
    // vertically-cleared edge, so this must stay far under the coplanar ceiling — a
    // regression that drops a prefilter makes building over a dense city O(edges) again
    private const float ValidateGradedCeilingMs = 30f;
    private const float TickCeilingMs = 8f;

    private static readonly string[] PerfKeys =
        { "perf.validate500_ms", "perf.validate500_graded_ms", "perf.tick300_ms" };

    /// <summary>Prefix for diagnostic-only metrics: recorded in kpi-latest.json and the
    /// markdown report for visibility, but never banded against the baseline and never
    /// written into kpi-baseline.json — they're new instrumentation, not something the
    /// M6 baseline run ever measured or asserted about.</summary>
    private const string DiagPrefix = "diag.";

    private static readonly string[] ExpectedKeys =
    {
        "signal.startup_lost_s", "signal.sat_headway_s",
        "yield4.minor_delay_mean_s", "yield4.minor_delay_p95_s", "yield4.completed",
        "grid.delay_index", "grid.stops_per_trip",
        "roundabout.delay_index", "roundabout.stops_per_trip", "roundabout.completed",
        "gradesep.at_grade_delay_s", "gradesep.bridged_delay_s",
        "gradesep.at_grade_completed", "gradesep.bridged_completed",
        "perf.validate500_ms", "perf.validate500_graded_ms", "perf.tick300_ms",
    };

    [Fact]
    public void DiagnosticMetricsAreEmittedButNeverBanded()
    {
        var metrics = KpiScenarios.SignalDischarge();
        for (int i = 1; i <= 5; i++)
            Assert.True(metrics.ContainsKey($"diag.signal.h{i}"), $"missing diag.signal.h{i}");

        // HISTORY: before the M6.5 anticipatory-spillback fix (task 4), this signal's
        // 12 s green cleared only ~4 of the 10 queued vehicles (h1..h4 summed to
        // ~11.9 s) — h5 was unavoidably the first entry of the *next* cycle, its
        // value dominated by the ~20 s red wait, and this test asserted h5 > h4 to
        // pin that "before" finding. The spillback fix removed the dead-stop-at-the-
        // line discharge pattern: at least 5 vehicles now clear a single 12 s green,
        // so h5 is a genuine intra-cycle car-following headway like h2..h4 and the
        // old cycle-boundary assertion no longer describes anything real (it passed
        // only by a 0.017 s coincidence — a latent flake, dropped).
        //
        // The genuine intra-cycle headways (h2..h5 — followers discharging within
        // the same green) must sit in a sane car-following band. The range is
        // intentionally wide enough to survive further tuning: headways should sit
        // near ~2-2.5 s post-spillback-fix, but never below 1 s.
        for (int i = 2; i <= 5; i++)
            Assert.InRange(metrics[$"diag.signal.h{i}"], 1f, 6f);
    }

    [Fact]
    public void GenerateHealthReport()
    {
        var metrics = new Dictionary<string, float>();
        foreach (var (k, v) in KpiScenarios.SignalDischarge()) metrics[k] = v;
        foreach (var (k, v) in KpiScenarios.Yield4Way()) metrics[k] = v;
        foreach (var (k, v) in KpiScenarios.GridCommute()) metrics[k] = v;
        foreach (var (k, v) in KpiScenarios.RoundaboutThroughput()) metrics[k] = v;
        foreach (var (k, v) in KpiScenarios.GradeSeparation()) metrics[k] = v;
        foreach (var (k, v) in KpiScenarios.Perf()) metrics[k] = v;

        foreach (var key in ExpectedKeys)
            Assert.True(metrics.ContainsKey(key), $"scenario run produced no value for metric '{key}'");

        // perf ceilings are absolute: checked on every run, bootstrap or not
        var ceilingFailures = new List<string>();
        foreach (var key in PerfKeys)
        {
            float ceiling = key switch
            {
                "perf.validate500_ms" => ValidateCeilingMs,
                "perf.validate500_graded_ms" => ValidateGradedCeilingMs,
                _ => TickCeilingMs,
            };
            if (metrics[key] >= ceiling)
                ceilingFailures.Add($"{key} = {metrics[key]:F3} ms >= absolute ceiling {ceiling} ms");
        }

        var healthDir = Path.Combine(FindRepoRoot(), "docs", "health");
        Directory.CreateDirectory(healthDir);
        var baselinePath = Path.Combine(healthDir, "kpi-baseline.json");
        var latestPath = Path.Combine(healthDir, "kpi-latest.json");
        var reportPath = Path.Combine(healthDir, $"{Milestone}.md");

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        var sortedMetrics = metrics.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        // diag.* keys are instrumentation-only: reported and persisted to kpi-latest.json
        // for visibility, but they never entered the M6 baseline and must never be band-
        // checked against it (there is nothing plausible to band them against yet).
        var bandedMetrics = sortedMetrics.Where(kv => !kv.Key.StartsWith(DiagPrefix, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (!File.Exists(baselinePath))
        {
            Assert.True(ceilingFailures.Count == 0, "perf ceiling exceeded on bootstrap run:\n" +
                string.Join("\n", ceilingFailures));

            File.WriteAllText(baselinePath, JsonSerializer.Serialize(bandedMetrics, jsonOpts));
            File.WriteAllText(latestPath, JsonSerializer.Serialize(sortedMetrics, jsonOpts));
            File.WriteAllText(reportPath, RenderReport(sortedMetrics, null));
            return; // bootstrap: PASS
        }

        var baseline = JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(baselinePath))!;

        var bandFailures = new List<string>();
        foreach (var (key, value) in bandedMetrics)
        {
            if (Array.IndexOf(PerfKeys, key) >= 0)
                continue;
            if (!baseline.TryGetValue(key, out var baseVal) || baseVal == 0f)
                continue;
            float pct = MathF.Abs(value - baseVal) / MathF.Abs(baseVal);
            if (pct > BandPct)
                bandFailures.Add($"{key}: {value:F3} vs baseline {baseVal:F3} (Δ{pct * 100f:F1}%, band +/-{BandPct * 100f:F0}%)");
        }

        File.WriteAllText(latestPath, JsonSerializer.Serialize(sortedMetrics, jsonOpts));
        File.WriteAllText(reportPath, RenderReport(sortedMetrics, baseline));

        var allFailures = ceilingFailures.Concat(bandFailures).ToArray();
        Assert.True(allFailures.Length == 0, "KPI health check failed:\n" + string.Join("\n", allFailures));
    }

    private static string RenderReport(Dictionary<string, float> metrics, Dictionary<string, float>? baseline)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Milestone} Health Report");
        sb.AppendLine();
        sb.AppendLine(baseline is null
            ? "Baseline bootstrap: this run established `docs/health/kpi-baseline.json`."
            : "Compared against `docs/health/kpi-baseline.json`.");
        sb.AppendLine();
        sb.AppendLine("| metric | value | baseline | delta% |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var (key, value) in metrics)
        {
            if (baseline is not null && baseline.TryGetValue(key, out var baseVal))
            {
                float pct = baseVal == 0f ? 0f : (value - baseVal) / MathF.Abs(baseVal) * 100f;
                string sign = pct >= 0 ? "+" : "";
                sb.AppendLine($"| {key} | {value:F3} | {baseVal:F3} | {sign}{pct:F1}% |");
            }
            else
            {
                sb.AppendLine($"| {key} | {value:F3} | - | - |");
            }
        }
        return sb.ToString();
    }

    /// <summary>Walk up from the test binary's output directory to the directory
    /// containing citybuilder.sln — the repo root, regardless of how/where
    /// `dotnet test` was invoked from.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "citybuilder.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException(
                "could not locate repo root (citybuilder.sln) walking up from " + AppContext.BaseDirectory);
        return dir.FullName;
    }
}
