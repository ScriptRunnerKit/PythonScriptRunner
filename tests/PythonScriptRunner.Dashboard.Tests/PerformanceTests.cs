using System.Diagnostics;
using System.Globalization;
using System.Net;
using PythonScriptRunner.Dashboard.Tests.Infrastructure;

namespace PythonScriptRunner.Dashboard.Tests;

/// <summary>
/// Spec 007, AC-13 (D-116): with 90 days of synthetic runs, the main reads answer with p95 ≤ 500 ms. The CI uses ~200 thousand
/// runs; <c>PSR_PERF_RUNS_PER_DAY=50000</c> runs the full medium volume (D-53) outside the CI.
/// </summary>
public sealed class PerformanceTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private const int Samples = 30;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Timing")]
    public async Task Spec007_AC13_MainReadsWith90DaysOfRuns_AnswerWithP95Under500Ms()
    {
        var perDay = int.TryParse(Environment.GetEnvironmentVariable("PSR_PERF_RUNS_PER_DAY"), CultureInfo.InvariantCulture, out var configured)
            ? configured
            : 2_250;
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await env.ScalarAsync<int>("SELECT psr.ensure_partitions(now() - interval '91 days', 14, 0, 0)");
        await using var app = await DashboardApp.StartAsync(env);
        await app.PublishStageAsync("perf", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        for (var i = 0; i < 40; i++)
        {
            await app.Admin.CreateStageAsync(new NewCatalogItem($"stage-{i:00}", $"Stage {i}"), PsrEnvironment.Author, Ct);
        }

        // Synthetic history: UUID v7 ids carrying the creation time (the partition of each run), statuses as in production.
        for (var day = 0; day < 90; day++)
        {
            await env.ScalarAsync<int>($$"""
                WITH rows AS (
                    SELECT date_trunc('milliseconds', now() - interval '{{day}} days' - random() * interval '1 day') AS ts,
                           (ARRAY['Succeeded', 'Succeeded', 'Succeeded', 'Succeeded', 'Failed', 'Cancelled'])[1 + floor(random() * 6)::int] AS status
                    FROM generate_series(1, {{perDay}})),
                inserted AS (
                    INSERT INTO psr.runs (id, created_at, kind, mode, stage_key, stage_version, trigger, triggered_by, status, inputs,
                                          queued_at, started_at, finished_at, error)
                    SELECT (lpad(to_hex(floor(extract(epoch FROM ts) * 1000)::bigint), 12, '0') || '7' || substr(md5(random()::text), 1, 3)
                            || '8' || substr(md5(random()::text), 1, 15))::uuid,
                           ts, 'Stage', 'Async', 'perf', 1, 'Library', 'system:library', status, '{"texto": "x"}'::jsonb,
                           ts, ts + interval '1 second', ts + interval '4 seconds',
                           CASE WHEN status = 'Failed' THEN '{"kind": "ScriptError", "message": "falhou"}'::jsonb END
                    FROM rows
                    RETURNING 1)
                SELECT count(*)::int FROM inserted
                """);
        }

        await env.ScalarAsync<int>("SELECT 1; ANALYZE psr.runs");
        var ids = await env.ColumnAsync<Guid>($"SELECT id FROM psr.runs ORDER BY random() LIMIT {Samples}");
        using var reader = await app.LoginAsync("leitor");

        var results = new Dictionary<string, TimeSpan>
        {
            ["/runs"] = await P95Async(reader, _ => "/psr/api/runs?limit=50"),
            ["/runs?status=Failed&from=-7d"] = await P95Async(reader, _ =>
                $"/psr/api/runs?status=Failed&limit=50&from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-7).ToString("O"))}"),
            ["/runs/{id}"] = await P95Async(reader, i => $"/psr/api/runs/{ids[i]}"),
            ["/stages"] = await P95Async(reader, _ => "/psr/api/stages?pageSize=25"),
            ["/overview"] = await P95Async(reader, _ => "/psr/api/overview"),
        };

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{perDay * 90} runs: " + string.Join(", ", results.Select(r => $"{r.Key} p95 {r.Value.TotalMilliseconds:0} ms")));
        results.Where(r => r.Value > TimeSpan.FromMilliseconds(500)).Select(r => $"{r.Key}: {r.Value.TotalMilliseconds:0} ms").ShouldBeEmpty();
    }

    private static async Task<TimeSpan> P95Async(HttpClient client, Func<int, string> path)
    {
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync(path(i), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var times = new List<TimeSpan>();
        for (var i = 0; i < Samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            using var response = await client.GetAsync(path(i), Ct);
            await response.Content.ReadAsByteArrayAsync(Ct);
            times.Add(Stopwatch.GetElapsedTime(start));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path(i));
        }

        times.Sort();
        return times[(int)Math.Ceiling(0.95 * times.Count) - 1];
    }
}
