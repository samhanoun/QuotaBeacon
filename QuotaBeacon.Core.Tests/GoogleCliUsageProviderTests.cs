using QuotaBeacon.Core.Models;
using QuotaBeacon.Core.Providers.Google;

namespace QuotaBeacon.Core.Tests;

public sealed class GoogleCliQuotaParserTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parser_reads_used_percent_reset_and_plan_from_terminal_output()
    {
        const string output = """
            [36mModel usage[0m
            Pro          ▬▬▬▬▬▬▬░░░ 37%  Resets: 6h 15m
            Flash        ▬▬▬▬▬▬▬▬▬░ 82%  Resets: 23h 5m
            Tier: Gemini Code Assist Enterprise
            """;

        var parsed = GoogleCliQuotaParser.Parse(output, Now, PercentMeaning.Used);

        Assert.Equal("Gemini Code Assist Enterprise", parsed.Plan);
        Assert.Collection(
            parsed.Windows,
            window =>
            {
                Assert.Equal("Flash", window.Label);
                Assert.Equal(82, window.UsedPercent);
                Assert.Equal(Now.AddHours(23).AddMinutes(5), window.ResetsAt);
            },
            window =>
            {
                Assert.Equal("Pro", window.Label);
                Assert.Equal(37, window.UsedPercent);
                Assert.Equal(Now.AddHours(6).AddMinutes(15), window.ResetsAt);
            });
    }

    [Fact]
    public void Parser_supports_remaining_percent_and_deduplicates_terminal_redraws()
    {
        const string output = """
            Your Plan: Google AI Pro
            Gemini 3.1 Pro 70% remaining · resets in 2h 30m
            Gemini 3.1 Pro 64% remaining · resets in 2h 25m
            Gemini 3 Flash 15% used · reset in 45m
            """;

        var parsed = GoogleCliQuotaParser.Parse(output, Now, PercentMeaning.Remaining);

        Assert.Equal("Google AI Pro", parsed.Plan);
        Assert.Equal(2, parsed.Windows.Count);
        var pro = parsed.Windows.Single(window => window.Label == "Gemini 3.1 Pro");
        Assert.Equal(36, pro.UsedPercent);
        Assert.Equal(Now.AddHours(2).AddMinutes(25), pro.ResetsAt);
        Assert.Equal(15, parsed.Windows.Single(window => window.Label == "Gemini 3 Flash").UsedPercent);
    }

    [Fact]
    public void Parser_reads_current_antigravity_multiline_quota_blocks()
    {
        const string output = """
            └ Model Quota
              Gemini 3.5 Flash (High)
              ███████████ ███████████ ███████████ ░░░░░░░░░░░ ░░░░░░░░░░░ 60%
              60% remaining · Refreshes in 167h 26m

              Gemini 3.1 Pro (Low)
              ░░░░░░░░░░░ ░░░░░░░░░░░ ░░░░░░░░░░░ ░░░░░░░░░░░ ░░░░░░░░░░░ 0%
              Refreshes in 94h 14m

              Claude Sonnet 4.6 (Thinking)
              ███████████ ███████████ ███████████ ███████████ ███████████ 100%
              Quota available
            """;

        var parsed = GoogleCliQuotaParser.Parse(output, Now, PercentMeaning.Remaining);

        Assert.Collection(
            parsed.Windows,
            window =>
            {
                Assert.Equal("Claude Sonnet 4.6 (Thinking)", window.Label);
                Assert.Equal(0, window.UsedPercent);
                Assert.Null(window.ResetsAt);
            },
            window =>
            {
                Assert.Equal("Gemini 3.1 Pro (Low)", window.Label);
                Assert.Equal(100, window.UsedPercent);
                Assert.Equal(Now.AddHours(94).AddMinutes(14), window.ResetsAt);
                Assert.Equal(TimeSpan.FromDays(7), window.Duration);
            },
            window =>
            {
                Assert.Equal("Gemini 3.5 Flash (High)", window.Label);
                Assert.Equal(40, window.UsedPercent);
                Assert.Equal(Now.AddHours(167).AddMinutes(26), window.ResetsAt);
                Assert.Equal(TimeSpan.FromDays(7), window.Duration);
            });
    }

    [Fact]
    public void Parser_keeps_antigravity_model_groups_with_duplicate_window_names_separate()
    {
        const string output = """
            Account: person@example.invalid GEMINI MODELS
            Models within this group: Gemini Flash, Gemini Pro
            Weekly Limit
            [ ██████████ ] 99.60%
            100% remaining · Refreshes in 166h 57m
            Five Hour Limit
            [ █████████░ ] 97.63%
            98% remaining · Refreshes in 3h 57m

            CLAUDE AND GPT MODELS
            Models within this group: Claude Opus, Claude Sonnet, GPT-OSS
            Weekly Limit
            [ ██████████ ] 100.00%
            Quota available
            Five Hour Limit
            [ ██████████ ] 100.00%
            Quota available
            """;

        var parsed = GoogleCliQuotaParser.Parse(output, Now, PercentMeaning.Remaining);

        Assert.Equal(4, parsed.Windows.Count);
        Assert.Equal(
            2.37,
            parsed.Windows.Single(window =>
                window.Label == "Gemini Models · Five Hour Limit").UsedPercent,
            precision: 2);
        Assert.Equal(
            0.4,
            parsed.Windows.Single(window =>
                window.Label == "Gemini Models · Weekly Limit").UsedPercent,
            precision: 2);
        Assert.Contains(
            parsed.Windows,
            window => window.Label == "Claude and GPT Models · Five Hour Limit" &&
                      window.Duration == TimeSpan.FromHours(5));
        Assert.Contains(
            parsed.Windows,
            window => window.Label == "Claude and GPT Models · Weekly Limit");
        Assert.Equal(4, parsed.Windows.Select(window => window.Key).Distinct().Count());
    }

    [Fact]
    public void Parser_removes_complete_ansi_sequences_from_colored_quota_rows()
    {
        var parsed = GoogleCliQuotaParser.Parse(
            "\u001b[36mPro\u001b[0m 25% used \u001b[33mResets in 4h\u001b[0m",
            Now,
            PercentMeaning.Used);

        var window = Assert.Single(parsed.Windows);
        Assert.Equal("Pro", window.Label);
        Assert.Equal("pro", window.Key);
        Assert.Equal(25, window.UsedPercent);
        Assert.Equal(Now.AddHours(4), window.ResetsAt);
    }

    [Fact]
    public void Parser_bounds_untrusted_terminal_output_and_ignores_non_quota_percentages()
    {
        var output = new string('x', GoogleCliQuotaParser.MaximumInputCharacters + 5_000) +
                     "\nDownload 90%\n";

        var parsed = GoogleCliQuotaParser.Parse(output, Now, PercentMeaning.Used);

        Assert.Empty(parsed.Windows);
        Assert.Null(parsed.Plan);
    }

    [Fact]
    public void Parser_rejects_relative_resets_that_overflow_or_exceed_the_31_day_cap()
    {
        const string output = """
            Extreme 37% used Resets in 999999999999999999999 days
            Combined 37% used Resets in 30 days 49h
            Maximum 37% used Resets in 31 days
            """;

        var parsed = GoogleCliQuotaParser.Parse(output, Now, PercentMeaning.Used);

        Assert.Null(parsed.Windows.Single(window => window.Label == "Extreme").ResetsAt);
        Assert.Null(parsed.Windows.Single(window => window.Label == "Combined").ResetsAt);
        Assert.Equal(
            Now.AddDays(31),
            parsed.Windows.Single(window => window.Label == "Maximum").ResetsAt);
    }

    [Fact]
    public void Parser_does_not_overflow_when_the_observation_time_is_near_its_maximum()
    {
        var observedAt = DateTimeOffset.MaxValue.AddDays(-1);

        var parsed = GoogleCliQuotaParser.Parse(
            "Pro 37% used Resets in 2 days",
            observedAt,
            PercentMeaning.Used);

        Assert.Null(Assert.Single(parsed.Windows).ResetsAt);
    }

    [Fact]
    public void Parser_replaces_unicode_format_controls_before_projecting_labels()
    {
        var parsed = GoogleCliQuotaParser.Parse(
            "Pro\u202EModel 37% used Resets in 2h",
            Now,
            PercentMeaning.Used);

        var window = Assert.Single(parsed.Windows);
        Assert.Equal("Pro Model", window.Label);
        Assert.Equal("pro-model", window.Key);
        Assert.DoesNotContain('\u202E', window.Label);
    }
}

public sealed class GoogleCliUsageProviderTests
{
    [Fact]
    public async Task Gemini_provider_projects_official_cli_output()
    {
        var now = new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero);
        var source = new StubCliUsageSource(new CliUsageReadResult(
            CliUsageReadStatus.Available,
            "Pro         ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬ 25%  Resets: 1:30 PM (1h 30m)",
            null));
        var provider = new GeminiUsageProvider(source, new FixedTimeProvider(now));

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("gemini", snapshot.ProviderId);
        Assert.Equal("Gemini CLI", snapshot.ProviderName);
        Assert.Equal(SnapshotStatus.Available, snapshot.Status);
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(25, window.UsedPercent);
        Assert.Equal(now.AddHours(1).AddMinutes(30), window.ResetsAt);
    }

    [Fact]
    public async Task Antigravity_provider_returns_actionable_not_installed_state()
    {
        var source = new StubCliUsageSource(new CliUsageReadResult(
            CliUsageReadStatus.NotInstalled,
            string.Empty,
            "Install the official Antigravity CLI (agy) to monitor this provider."));
        var provider = new AntigravityUsageProvider(source, TimeProvider.System);

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Unavailable, snapshot.Status);
        Assert.Empty(snapshot.Windows);
        Assert.Contains("agy", snapshot.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CliUsageReadStatus.NotInstalled, SnapshotStatus.Unavailable)]
    [InlineData(CliUsageReadStatus.Failed, SnapshotStatus.Error)]
    public async Task Gemini_provider_maps_cli_failures_without_exposing_output(
        CliUsageReadStatus readStatus,
        SnapshotStatus expectedStatus)
    {
        var source = new StubCliUsageSource(new CliUsageReadResult(
            readStatus,
            "private terminal output",
            "Safe provider diagnostic."));
        var provider = new GeminiUsageProvider(source, TimeProvider.System);

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(expectedStatus, snapshot.Status);
        Assert.Equal("Safe provider diagnostic.", snapshot.Diagnostic);
        Assert.DoesNotContain("private", snapshot.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gemini_provider_returns_a_bounded_diagnostic_when_output_has_no_quota()
    {
        var source = new StubCliUsageSource(new CliUsageReadResult(
            CliUsageReadStatus.Available,
            "No quota information is available for this account.",
            null));
        var provider = new GeminiUsageProvider(source, TimeProvider.System);

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Unavailable, snapshot.Status);
        Assert.Empty(snapshot.Windows);
        Assert.Contains("did not report quota information", snapshot.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gemini_provider_explains_the_fresh_read_only_session_limitation()
    {
        var source = new StubCliUsageSource(new CliUsageReadResult(
            CliUsageReadStatus.Available,
            "No API calls have been made in this session.",
            null));
        var provider = new GeminiUsageProvider(source, TimeProvider.System);

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Unavailable, snapshot.Status);
        Assert.Empty(snapshot.Windows);
        Assert.Contains("fresh read-only session", snapshot.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will not make a model request", snapshot.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("run /stats model once", snapshot.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gemini_provider_points_empty_quota_output_to_stats_model()
    {
        var source = new StubCliUsageSource(new CliUsageReadResult(
            CliUsageReadStatus.Available,
            string.Empty,
            null));
        var provider = new GeminiUsageProvider(source, TimeProvider.System);

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Unavailable, snapshot.Status);
        Assert.Contains("/stats model", snapshot.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("/model", snapshot.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Antigravity_provider_returns_bounded_one_time_workspace_trust_guidance()
    {
        var probeDirectory = Path.Combine(
            Path.GetTempPath(),
            "QuotaBeacon",
            "cli-probe",
            "antigravity");
        var source = new StubCliUsageSource(new CliUsageReadResult(
            CliUsageReadStatus.Available,
            new string('x', GoogleCliQuotaParser.MaximumInputCharacters) +
            "\u001b[33mThis workspace requires permission to read, edit, and execute files here. Yes / No\u001b[0m SENSITIVE_RAW_OUTPUT",
            null));
        var provider = new AntigravityUsageProvider(source, TimeProvider.System, probeDirectory);

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Unavailable, snapshot.Status);
        Assert.Empty(snapshot.Windows);
        var diagnostic = Assert.IsType<string>(snapshot.Diagnostic);
        Assert.Contains("one-time", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("run agy once", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("QuotaBeacon", diagnostic, StringComparison.Ordinal);
        Assert.Contains(probeDirectory, diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SENSITIVE_RAW_OUTPUT", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', diagnostic);
        Assert.InRange(diagnostic.Length, 1, 1024);
    }

    [Fact]
    public async Task Gemini_provider_guides_the_user_to_the_current_stats_command_when_the_cli_returns_no_parseable_quota()
    {
        var source = new StubCliUsageSource(new CliUsageReadResult(
            CliUsageReadStatus.Available,
            "Welcome back to Gemini CLI.",
            null));
        var provider = new GeminiUsageProvider(source, TimeProvider.System);

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Contains("/stats model", snapshot.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/model once", snapshot.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Antigravity_provider_projects_remaining_quota_as_used_percentage()
    {
        var now = new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero);
        var source = new StubCliUsageSource(new CliUsageReadResult(
            CliUsageReadStatus.Available,
            "Pro 75% remaining Resets in 4h",
            null));
        var provider = new AntigravityUsageProvider(source, new FixedTimeProvider(now));

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("antigravity", snapshot.ProviderId);
        Assert.Equal(SnapshotStatus.Available, snapshot.Status);
        Assert.Equal(25, Assert.Single(snapshot.Windows).UsedPercent);
    }

    [Fact]
    public async Task Antigravity_provider_surfaces_a_one_time_workspace_trust_diagnostic()
    {
        var source = new StubCliUsageSource(new CliUsageReadResult(
            CliUsageReadStatus.Available,
            """
            Accessing workspace:

            C:\Workspace

            Do you trust the contents of this project?

            Antigravity CLI requires permission to read, edit, and execute files here.
            > Yes, I trust this folder
              No, exit
            """,
            null));
        var provider = new AntigravityUsageProvider(source, TimeProvider.System);

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(SnapshotStatus.Unavailable, snapshot.Status);
        Assert.Contains("trust approval", snapshot.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubCliUsageSource(CliUsageReadResult result) : ICliUsageSource
    {
        public Task<CliUsageReadResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
