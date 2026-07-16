using System.Text;
using System.Text.Json;
using Xunit;

namespace CityBuilder.Domain.Tests.Kpi;

/// <summary>M6 KPI harness: runs every deterministic scenario in
/// <see cref="KpiScenarios"/>, merges their metrics, and writes the per-milestone
/// health report under docs/health/. First run (no baseline on disk) bootstraps the
/// baseline and passes; every later run asserts non-perf metrics stay within a ±25%
/// band of the baseline and perf metrics stay under their absolute ceilings, then
/// refreshes kpi-latest.json + M6.md. Perf ceilings are never banded — they're
/// hard budgets regardless of history.</summary>
public class KpiSuiteTests
{
    private const float BandPct = 0.25f;
    private const float ValidateCeilingMs = 150f;
    private const float TickCeilingMs = 8f;

    private static readonly string[] PerfKeys = { "perf.validate500_ms", "perf.tick300_ms" };

    private static readonly string[] ExpectedKeys =
    {
        "signal.startup_lost_s", "signal.sat_headway_s",
        "yield4.minor_delay_mean_s", "yield4.minor_delay_p95_s", "yield4.completed",
        "grid.delay_index", "grid.stops_per_trip",
        "perf.validate500_ms", "perf.tick300_ms",
    };

    [Fact]
    public void GenerateHealthReport()
    {
        var metrics = new Dictionary<string, float>();
        foreach (var (k, v) in KpiScenarios.SignalDischarge()) metrics[k] = v;
        foreach (var (k, v) in KpiScenarios.Yield4Way()) metrics[k] = v;
        foreach (var (k, v) in KpiScenarios.GridCommute()) metrics[k] = v;
        foreach (var (k, v) in KpiScenarios.Perf()) metrics[k] = v;

        foreach (var key in ExpectedKeys)
            Assert.True(metrics.ContainsKey(key), $"scenario run produced no value for metric '{key}'");

        // perf ceilings are absolute: checked on every run, bootstrap or not
        var ceilingFailures = new List<string>();
        foreach (var key in PerfKeys)
        {
            float ceiling = key == "perf.validate500_ms" ? ValidateCeilingMs : TickCeilingMs;
            if (metrics[key] >= ceiling)
                ceilingFailures.Add($"{key} = {metrics[key]:F3} ms >= absolute ceiling {ceiling} ms");
        }

        var healthDir = Path.Combine(FindRepoRoot(), "docs", "health");
        Directory.CreateDirectory(healthDir);
        var baselinePath = Path.Combine(healthDir, "kpi-baseline.json");
        var latestPath = Path.Combine(healthDir, "kpi-latest.json");
        var reportPath = Path.Combine(healthDir, "M6.md");

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        var sortedMetrics = metrics.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (!File.Exists(baselinePath))
        {
            Assert.True(ceilingFailures.Count == 0, "perf ceiling exceeded on bootstrap run:\n" +
                string.Join("\n", ceilingFailures));

            var json = JsonSerializer.Serialize(sortedMetrics, jsonOpts);
            File.WriteAllText(baselinePath, json);
            File.WriteAllText(latestPath, json);
            File.WriteAllText(reportPath, RenderReport(sortedMetrics, null));
            return; // bootstrap: PASS
        }

        var baseline = JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(baselinePath))!;

        var bandFailures = new List<string>();
        foreach (var (key, value) in sortedMetrics)
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
        sb.AppendLine("# M6 Health Report");
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
