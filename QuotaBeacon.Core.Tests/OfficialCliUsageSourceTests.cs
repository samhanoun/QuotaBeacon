using QuotaBeacon.Core.Providers.Google;

namespace QuotaBeacon.Core.Tests;

public sealed class OfficialCliUsageSourceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"quota beacon cli {Guid.NewGuid():N}");
    private readonly List<string> _environmentVariables = [];

    [Fact]
    public async Task Source_runs_a_fully_qualified_executable_in_a_pseudo_terminal()
    {
        Directory.CreateDirectory(_directory);
        var terminalStatePath = Path.Combine(_directory, "direct-terminal-state.txt");
        var environmentVariable = NewEnvironmentVariable();
        Environment.SetEnvironmentVariable(environmentVariable, PowerShellPath());
        var source = CreateSource(
            environmentVariable,
            "$state = [Console]::IsInputRedirected.ToString() + ',' + [Console]::IsOutputRedirected.ToString(); " +
            $"[IO.File]::WriteAllText('{terminalStatePath}', $state); 'QB_DIRECT_CAPTURE'");

        var result = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(CliUsageReadStatus.Available, result.Status);
        Assert.Contains("QB_DIRECT_CAPTURE", result.Output, StringComparison.Ordinal);
        Assert.Equal(
            "False,False",
            await File.ReadAllTextAsync(terminalStatePath, TestContext.Current.CancellationToken));
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public async Task Source_wraps_a_command_script_and_captures_the_requested_slash_command()
    {
        Directory.CreateDirectory(_directory);
        var scriptPath = Path.Combine(_directory, "quota fixture.cmd");
        var startedPath = Path.Combine(_directory, "quota-started.txt");
        var terminalStatePath = Path.Combine(_directory, "terminal-state.txt");
        await File.WriteAllTextAsync(
            scriptPath,
            "@echo off\r\n" +
            $">\"{startedPath}\" echo started\r\n" +
            "echo QB_SCRIPT_STARTED\r\n" +
            $"powershell.exe -NoProfile -NonInteractive -Command \"$state = [Console]::IsInputRedirected.ToString() + ',' + [Console]::IsOutputRedirected.ToString(); [IO.File]::WriteAllText('{terminalStatePath}', $state); if ($state -ne 'False,False') {{ 'QB_NOT_TTY' }} else {{ 'QB_TTY_READY' }}\"\r\n" +
            "set /p \"QB_LINE=\"\r\n" +
            "echo QB_CAPTURE:%QB_LINE%\r\n" +
            "set /p \"QB_SECOND=\"\r\n" +
            "echo QB_UNEXPECTED_SECOND:%QB_SECOND%\r\n",
            CancellationToken.None);
        var environmentVariable = NewEnvironmentVariable();
        Environment.SetEnvironmentVariable(environmentVariable, scriptPath);
        var source = CreateSource(environmentVariable, "/usage", ["quota fixture.cmd"]);

        var result = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(CliUsageReadStatus.Available, result.Status);
        Assert.True(File.Exists(startedPath), "The command script did not reach its first instruction.");
        Assert.Equal(
            "False,False",
            await File.ReadAllTextAsync(terminalStatePath, TestContext.Current.CancellationToken));
        Assert.True(
            result.Output.Contains("QB_TTY_READY", StringComparison.Ordinal),
            Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(result.Output)));
        Assert.Contains("QB_CAPTURE:/usage", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("QB_NOT_TTY", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("QB_UNEXPECTED_SECOND", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("/quit", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/exit", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Source_reports_a_missing_tool_without_starting_a_shell()
    {
        var missingVariable = NewEnvironmentVariable();
        var source = new OfficialCliUsageSource(
            [$"quotabeacon-missing-{Guid.NewGuid():N}.exe"],
            missingVariable,
            "/usage",
            "Install the official CLI.");

        var result = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(CliUsageReadStatus.NotInstalled, result.Status);
        Assert.Empty(result.Output);
        Assert.Equal("Install the official CLI.", result.Diagnostic);
    }

    [Fact]
    public async Task Source_bounds_terminal_output_and_contains_timeouts()
    {
        var environmentVariable = NewEnvironmentVariable();
        Environment.SetEnvironmentVariable(environmentVariable, CommandProcessorPath());
        var boundedSource = new OfficialCliUsageSource(
            ["quotabeacon-never-used.exe"],
            environmentVariable,
            "for /L %i in (1,1,10000) do @echo QB_OUTPUT_%i_ABCDEFGHIJKLMNOPQRSTUVWXYZ",
            "missing",
            startupDelay: TimeSpan.FromMilliseconds(25),
            captureDelay: TimeSpan.FromMilliseconds(100),
            timeout: TimeSpan.FromSeconds(3),
            maximumOutputCharacters: 4096);

        var bounded = await boundedSource.ReadAsync(CancellationToken.None);

        Assert.Equal(CliUsageReadStatus.Available, bounded.Status);
        Assert.InRange(bounded.Output.Length, 1, 4096);

        var timeoutSource = new OfficialCliUsageSource(
            ["quotabeacon-never-used.exe"],
            environmentVariable,
            "/usage",
            "missing",
            startupDelay: TimeSpan.FromSeconds(2),
            captureDelay: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMilliseconds(100),
            maximumOutputCharacters: 4096);

        var timedOut = await timeoutSource.ReadAsync(CancellationToken.None);

        Assert.Equal(CliUsageReadStatus.Failed, timedOut.Status);
        Assert.Equal("The official CLI did not return usage data in time.", timedOut.Diagnostic);
    }

    [Fact]
    public async Task Cancellation_terminates_the_entire_cli_process_tree()
    {
        Directory.CreateDirectory(_directory);
        var startedPath = Path.Combine(_directory, "started.txt");
        var escapedPath = Path.Combine(_directory, "escaped.txt");
        var descendantPath = Path.Combine(_directory, "descendant.cmd");
        var scriptPath = Path.Combine(_directory, "tree fixture.cmd");
        await File.WriteAllTextAsync(
            descendantPath,
            "@echo off\r\n" +
            "ping -n 3 127.0.0.1 >nul\r\n" +
            $">\"{escapedPath}\" echo escaped\r\n",
            CancellationToken.None);
        await File.WriteAllTextAsync(
            scriptPath,
            "@echo off\r\n" +
            $">\"{startedPath}\" echo started\r\n" +
            $"start \"\" /b cmd.exe /d /s /c \"\"{descendantPath}\"\"\r\n" +
            "echo QB_TREE_READY\r\n" +
            "set /p \"QB_LINE=\"\r\n" +
            "ping -n 20 127.0.0.1 >nul\r\n",
            CancellationToken.None);
        var environmentVariable = NewEnvironmentVariable();
        Environment.SetEnvironmentVariable(environmentVariable, scriptPath);
        var source = new OfficialCliUsageSource(
            ["tree fixture.cmd"],
            environmentVariable,
            "/usage",
            "missing",
            startupDelay: TimeSpan.FromMilliseconds(25),
            captureDelay: TimeSpan.FromSeconds(10),
            timeout: TimeSpan.FromSeconds(12),
            maximumOutputCharacters: 4096);
        using var cancellation = new CancellationTokenSource();

        var readTask = source.ReadAsync(cancellation.Token);
        await WaitForFileAsync(startedPath, TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(File.Exists(escapedPath), "A descendant survived cancellation and wrote its marker.");
    }

    public void Dispose()
    {
        foreach (var environmentVariable in _environmentVariables)
        {
            Environment.SetEnvironmentVariable(environmentVariable, null);
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static OfficialCliUsageSource CreateSource(
        string environmentVariable,
        string command,
        IReadOnlyList<string>? executableNames = null) =>
        new(
            executableNames ?? ["quotabeacon-never-used.exe"],
            environmentVariable,
            command,
            "missing",
            startupDelay: TimeSpan.FromSeconds(4),
            captureDelay: TimeSpan.FromSeconds(3),
            timeout: TimeSpan.FromSeconds(10),
            maximumOutputCharacters: 4096);

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellation.Token);
        }
    }

    private string NewEnvironmentVariable()
    {
        var name = $"QUOTABEACON_TEST_CLI_{Guid.NewGuid():N}";
        _environmentVariables.Add(name);
        return name;
    }

    private static string CommandProcessorPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "cmd.exe");

    private static string PowerShellPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");
}
