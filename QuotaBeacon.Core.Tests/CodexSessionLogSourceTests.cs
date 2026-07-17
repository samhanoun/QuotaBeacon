using System.Text.Json;
using QuotaBeacon.Core.Models;
using QuotaBeacon.Core.Providers.Codex;

namespace QuotaBeacon.Core.Tests;

public sealed class CodexSessionLogSourceTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"quotabeacon-codex-{Guid.NewGuid():N}");

    [Fact]
    public async Task Source_reads_the_newest_quota_events_across_session_files()
    {
        var older = Path.Combine(_home, "sessions", "2026", "07", "15", "old.jsonl");
        var newer = Path.Combine(_home, "sessions", "2026", "07", "16", "new.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(older)!);
        Directory.CreateDirectory(Path.GetDirectoryName(newer)!);
        await File.WriteAllLinesAsync(older, [CodexLine("2026-07-15T10:00:00Z", 10)], CancellationToken.None);
        await File.WriteAllLinesAsync(
            newer,
            [
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"private content\"}}",
                CodexLine("2026-07-16T11:00:00Z", 55)
            ],
            CancellationToken.None);
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 7, 16, 11, 0, 0, DateTimeKind.Utc));
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var source = new CodexSessionLogSource(_home, new FixedTimeProvider(now));

        var snapshot = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(SnapshotSource.LocalFallback, snapshot.Source);
        Assert.Equal(55, Assert.Single(snapshot.Windows).UsedPercent);
        Assert.Equal(now, snapshot.ObservedAt);
        Assert.DoesNotContain("private", snapshot.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Source_reports_missing_session_metadata_safely()
    {
        var source = new CodexSessionLogSource(_home, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<ProviderDataException>(
            () => source.ReadAsync(CancellationToken.None));

        Assert.Equal("No local Codex quota snapshot is available yet.", exception.Message);
    }

    private static string CodexLine(string timestamp, double used) => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            rate_limits = new
            {
                limit_id = "codex",
                plan_type = "pro",
                primary = new
                {
                    used_percent = used,
                    window_minutes = 300,
                    resets_at = 1784210400
                }
            }
        }
    });

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }
}
