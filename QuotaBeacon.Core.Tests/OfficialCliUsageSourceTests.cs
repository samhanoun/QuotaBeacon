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
        var interactiveScriptPath = Path.Combine(_directory, "quota fixture.ps1");
        var startedPath = Path.Combine(_directory, "quota-started.txt");
        var terminalStatePath = Path.Combine(_directory, "terminal-state.txt");
        var currentDirectoryPath = Path.Combine(_directory, "current-directory.txt");
        var workingDirectory = Path.Combine(_directory, "stable probe directory");
        Directory.CreateDirectory(workingDirectory);
        await File.WriteAllTextAsync(
            interactiveScriptPath,
            "$ErrorActionPreference = 'Stop'\r\n" +
            $"[IO.File]::WriteAllText({PowerShellLiteral(startedPath)}, 'started')\r\n" +
            $"[IO.File]::WriteAllText({PowerShellLiteral(currentDirectoryPath)}, [Environment]::CurrentDirectory)\r\n" +
            "$state = [Console]::IsInputRedirected.ToString() + ',' + [Console]::IsOutputRedirected.ToString()\r\n" +
            $"[IO.File]::WriteAllText({PowerShellLiteral(terminalStatePath)}, $state)\r\n" +
            "if ($state -ne 'False,False') { Write-Output 'QB_NOT_TTY' } else { Write-Output 'QB_TTY_READY' }\r\n" +
            "$expected = '/stats model'\r\n" +
            "$typed = [Text.StringBuilder]::new()\r\n" +
            "while ($typed.Length -lt $expected.Length) { [void]$typed.Append([Console]::ReadKey($true).KeyChar) }\r\n" +
            "Write-Output 'QB_COMMAND_READY'\r\n" +
            "$enter = [Console]::ReadKey($true)\r\n" +
            "if ($enter.Key -eq [ConsoleKey]::Enter) { Write-Output ('QB_CAPTURE:' + $typed.ToString()) }\r\n" +
            "$second = [Console]::ReadKey($true)\r\n" +
            "Write-Output ('QB_UNEXPECTED_SECOND:' + $second.KeyChar)\r\n",
            CancellationToken.None);
        await File.WriteAllTextAsync(
            scriptPath,
            "@echo off\r\n" +
            $"\"{PowerShellPath()}\" -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{interactiveScriptPath}\"\r\n",
            CancellationToken.None);
        var environmentVariable = NewEnvironmentVariable();
        Environment.SetEnvironmentVariable(environmentVariable, scriptPath);
        var source = CreateSource(
            environmentVariable,
            "/stats model",
            ["quota fixture.cmd"],
            workingDirectory,
            "QB_TTY_READY",
            "QB_COMMAND_READY");

        var result = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(CliUsageReadStatus.Available, result.Status);
        Assert.True(File.Exists(startedPath), "The command script did not reach its first instruction.");
        Assert.Equal(
            "False,False",
            await File.ReadAllTextAsync(terminalStatePath, TestContext.Current.CancellationToken));
        Assert.Equal(
            Path.GetFullPath(workingDirectory),
            (await File.ReadAllTextAsync(
                currentDirectoryPath,
                TestContext.Current.CancellationToken)).Trim(),
            ignoreCase: true);
        Assert.True(
            result.Output.Contains("QB_TTY_READY", StringComparison.Ordinal),
            Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(result.Output)));
        Assert.Contains("QB_CAPTURE:/stats model", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("QB_NOT_TTY", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("QB_UNEXPECTED_SECOND", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("/quit", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/exit", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locator_prefers_official_then_process_user_and_machine_directories()
    {
        var processDirectory = CreateDirectory("process path");
        var userDirectory = CreateDirectory("user path");
        var machineDirectory = CreateDirectory("machine path");
        var officialDirectory = CreateDirectory("official directory");
        var processExecutable = CreateExecutable(processDirectory, "agy.exe");
        var userExecutable = CreateExecutable(userDirectory, "agy.exe");
        var machineExecutable = CreateExecutable(machineDirectory, "agy.exe");
        var officialExecutable = CreateExecutable(officialDirectory, "agy.exe");

        AssertPathEqual(
            officialExecutable,
            OfficialCliUsageSource.FindExecutable(
                ["agy.exe"],
                configuredExecutable: null,
                processDirectory,
                userDirectory,
                machineDirectory,
                [officialDirectory]));

        File.Delete(officialExecutable);
        AssertPathEqual(
            processExecutable,
            OfficialCliUsageSource.FindExecutable(
                ["agy.exe"],
                configuredExecutable: null,
                processDirectory,
                userDirectory,
                machineDirectory,
                [officialDirectory]));

        File.Delete(processExecutable);
        AssertPathEqual(
            userExecutable,
            OfficialCliUsageSource.FindExecutable(
                ["agy.exe"],
                configuredExecutable: null,
                processDirectory,
                userDirectory,
                machineDirectory,
                [officialDirectory]));

        File.Delete(userExecutable);
        AssertPathEqual(
            machineExecutable,
            OfficialCliUsageSource.FindExecutable(
                ["agy.exe"],
                configuredExecutable: null,
                processDirectory,
                userDirectory,
                machineDirectory,
                [officialDirectory]));
    }

    [Fact]
    public void Locator_expands_registry_style_environment_variables_in_path_entries()
    {
        var expandedDirectory = CreateDirectory("expanded user path");
        var executable = CreateExecutable(expandedDirectory, "gemini.cmd");
        var variable = NewEnvironmentVariable();
        Environment.SetEnvironmentVariable(variable, expandedDirectory);

        var located = OfficialCliUsageSource.FindExecutable(
            ["gemini.cmd"],
            configuredExecutable: null,
            processPath: null,
            userPath: $"%{variable}%",
            machinePath: null,
            officialDirectories: []);

        AssertPathEqual(executable, located);
    }

    [Fact]
    public void Locator_rejects_unsafe_names_and_relative_official_directories()
    {
        var officialDirectory = CreateDirectory("safe official directory");
        _ = CreateExecutable(_directory, "agy.exe");

        var escapedName = OfficialCliUsageSource.FindExecutable(
            [Path.Combine("..", "agy.exe")],
            configuredExecutable: null,
            processPath: null,
            userPath: null,
            machinePath: null,
            [officialDirectory]);
        var relativeDirectory = OfficialCliUsageSource.FindExecutable(
            ["agy.exe"],
            configuredExecutable: null,
            processPath: null,
            userPath: null,
            machinePath: null,
            ["relative"]);

        Assert.Null(escapedName);
        Assert.Null(relativeDirectory);
    }

    [Fact]
    public async Task Submission_types_only_the_command_as_keystrokes_then_enter()
    {
        var writes = new List<byte[]>();

        await WindowsPseudoConsoleSession.SubmitLineAsync(
            "/stats model",
            (buffer, token) =>
            {
                token.ThrowIfCancellationRequested();
                writes.Add(buffer.ToArray());
                return ValueTask.CompletedTask;
            },
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.Equal("/stats model".Length + 1, writes.Count);
        Assert.All(writes, write => Assert.Single(write));
        Assert.Equal(
            "/stats model",
            System.Text.Encoding.UTF8.GetString(writes.Take(writes.Count - 1).SelectMany(write => write).ToArray()));
        Assert.Equal("\r", System.Text.Encoding.UTF8.GetString(writes[^1]));
        Assert.DoesNotContain(
            writes,
            write => System.Text.Encoding.UTF8.GetString(write).Contains(
                "/quit",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            writes,
            write => System.Text.Encoding.UTF8.GetString(write).Contains(
                "/exit",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cancellation_between_command_and_enter_does_not_send_more_input()
    {
        var writes = new List<byte[]>();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsPseudoConsoleSession.SubmitLineAsync(
                "/stats model",
                (buffer, _) =>
                {
                    writes.Add(buffer.ToArray());
                    cancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(1),
                cancellation.Token).AsTask());

        var firstKey = Assert.Single(writes);
        Assert.Equal("/", System.Text.Encoding.UTF8.GetString(firstKey));
    }

    [Fact]
    public async Task Source_uses_the_configured_working_directory_for_interactive_cli_startup()
    {
        Directory.CreateDirectory(_directory);
        var scriptPath = Path.Combine(_directory, "working-dir-fixture.cmd");
        var currentDirectoryPath = Path.Combine(_directory, "current-directory.txt");
        await File.WriteAllTextAsync(
            scriptPath,
            "@echo off\r\n" +
            $"cd >\"{currentDirectoryPath}\"\r\n" +
            "echo QB_READY\r\n" +
            "set /p \"QB_LINE=\"\r\n" +
            "echo QB_CAPTURE:%QB_LINE%\r\n",
            CancellationToken.None);
        var environmentVariable = NewEnvironmentVariable();
        Environment.SetEnvironmentVariable(environmentVariable, scriptPath);
        var workingDirectory = Path.Combine(_directory, "cli-working-directory");
        Directory.CreateDirectory(workingDirectory);
        var source = new OfficialCliUsageSource(
            ["working-dir-fixture.cmd"],
            environmentVariable,
            "/usage",
            "missing",
            startupDelay: TimeSpan.FromSeconds(4),
            captureDelay: TimeSpan.FromSeconds(3),
            timeout: TimeSpan.FromSeconds(15),
            maximumOutputCharacters: 4096,
            workingDirectory: workingDirectory);

        var result = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(CliUsageReadStatus.Available, result.Status);
        Assert.Contains("QB_CAPTURE:/usage", result.Output, StringComparison.Ordinal);
        Assert.Equal(
            workingDirectory,
            (await File.ReadAllTextAsync(currentDirectoryPath, TestContext.Current.CancellationToken)).Trim(),
            ignoreCase: true);
    }

    [Fact]
    public async Task Source_never_submits_enter_when_a_workspace_trust_prompt_blocks_input()
    {
        Directory.CreateDirectory(_directory);
        var scriptPath = Path.Combine(_directory, "trust-prompt-fixture.cmd");
        var submittedPath = Path.Combine(_directory, "trust-was-submitted.txt");
        await File.WriteAllTextAsync(
            scriptPath,
            "@echo off\r\n" +
            "echo Antigravity CLI requires permission to read, edit, and execute files here.\r\n" +
            "echo Yes, I trust this folder / No, exit\r\n" +
            "set /p \"QB_LINE=\"\r\n" +
            $">\"{submittedPath}\" echo %QB_LINE%\r\n",
            CancellationToken.None);
        var environmentVariable = NewEnvironmentVariable();
        Environment.SetEnvironmentVariable(environmentVariable, scriptPath);
        var source = new OfficialCliUsageSource(
            ["trust-prompt-fixture.cmd"],
            environmentVariable,
            "/usage",
            "missing",
            startupDelay: TimeSpan.FromSeconds(1),
            captureDelay: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(8),
            maximumOutputCharacters: 4096,
            commandReadyMarker: "/usage View model quota usage",
            inputBlockedMarkers:
            [
                "requires permission to read, edit, and execute files here"
            ],
            commandReadyTimeout: TimeSpan.FromSeconds(3));

        var result = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(CliUsageReadStatus.Available, result.Status);
        Assert.Contains("requires permission", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(submittedPath), "The trust prompt received an Enter key.");
    }

    [Fact]
    public async Task Source_never_submits_enter_when_a_trust_prompt_arrives_after_command_readiness()
    {
        Directory.CreateDirectory(_directory);
        var scriptPath = Path.Combine(_directory, "late-trust-fixture.cmd");
        var interactiveScriptPath = Path.Combine(_directory, "late-trust-fixture.ps1");
        var submittedPath = Path.Combine(_directory, "late-trust-was-submitted.txt");
        await File.WriteAllTextAsync(
            interactiveScriptPath,
            "Write-Output 'QB_INPUT_READY'\r\n" +
            "$expected = '/usage'\r\n" +
            "$typed = [Text.StringBuilder]::new()\r\n" +
            "while ($typed.Length -lt $expected.Length) { [void]$typed.Append([Console]::ReadKey($true).KeyChar) }\r\n" +
            "Write-Output 'QB_COMMAND_READY'\r\n" +
            "Start-Sleep -Milliseconds 40\r\n" +
            "Write-Output 'Antigravity requires permission to read, edit, and execute files here'\r\n" +
            "$next = [Console]::ReadKey($true)\r\n" +
            $"if ($next.Key -eq [ConsoleKey]::Enter) {{ [IO.File]::WriteAllText({PowerShellLiteral(submittedPath)}, 'ENTER') }}\r\n",
            CancellationToken.None);
        await File.WriteAllTextAsync(
            scriptPath,
            "@echo off\r\n" +
            $"\"{PowerShellPath()}\" -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{interactiveScriptPath}\"\r\n",
            CancellationToken.None);
        var environmentVariable = NewEnvironmentVariable();
        Environment.SetEnvironmentVariable(environmentVariable, scriptPath);
        var source = new OfficialCliUsageSource(
            ["late-trust-fixture.cmd"],
            environmentVariable,
            "/usage",
            "missing",
            startupDelay: TimeSpan.FromSeconds(1),
            captureDelay: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(10),
            maximumOutputCharacters: 4096,
            inputReadyMarker: "QB_INPUT_READY",
            commandReadyMarker: "QB_COMMAND_READY",
            inputBlockedMarkers:
            [
                "requires permission to read, edit, and execute files here"
            ],
            commandReadyTimeout: TimeSpan.FromSeconds(4));

        var result = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(CliUsageReadStatus.Available, result.Status);
        Assert.Contains("requires permission", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(submittedPath), "A late trust prompt received the command Enter key.");
    }

    [Fact]
    public async Task Source_ignores_a_stale_command_marker_rendered_before_typing()
    {
        Directory.CreateDirectory(_directory);
        var scriptPath = Path.Combine(_directory, "stale-marker-fixture.cmd");
        var interactiveScriptPath = Path.Combine(_directory, "stale-marker-fixture.ps1");
        var submittedPath = Path.Combine(_directory, "stale-marker-was-submitted.txt");
        await File.WriteAllTextAsync(
            interactiveScriptPath,
            "Write-Output 'QB_INPUT_READY'\r\n" +
            "Write-Output 'QB_COMMAND_READY'\r\n" +
            "$expected = '/usage'\r\n" +
            "$typed = [Text.StringBuilder]::new()\r\n" +
            "while ($typed.Length -lt $expected.Length) { [void]$typed.Append([Console]::ReadKey($true).KeyChar) }\r\n" +
            "$next = [Console]::ReadKey($true)\r\n" +
            $"if ($next.Key -eq [ConsoleKey]::Enter) {{ [IO.File]::WriteAllText({PowerShellLiteral(submittedPath)}, 'ENTER') }}\r\n",
            CancellationToken.None);
        await File.WriteAllTextAsync(
            scriptPath,
            "@echo off\r\n" +
            $"\"{PowerShellPath()}\" -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{interactiveScriptPath}\"\r\n",
            CancellationToken.None);
        var environmentVariable = NewEnvironmentVariable();
        Environment.SetEnvironmentVariable(environmentVariable, scriptPath);
        var source = new OfficialCliUsageSource(
            ["stale-marker-fixture.cmd"],
            environmentVariable,
            "/usage",
            "missing",
            startupDelay: TimeSpan.FromSeconds(1),
            captureDelay: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(10),
            maximumOutputCharacters: 4096,
            inputReadyMarker: "QB_INPUT_READY",
            commandReadyMarker: "QB_COMMAND_READY",
            commandReadyTimeout: TimeSpan.FromSeconds(3));

        var result = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(CliUsageReadStatus.Failed, result.Status);
        Assert.Contains("command menu did not become ready", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(submittedPath), "A stale menu marker authorized Enter.");
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
        IReadOnlyList<string>? executableNames = null,
        string? workingDirectory = null,
        string? inputReadyMarker = null,
        string? commandReadyMarker = null) =>
        new(
            executableNames ?? ["quotabeacon-never-used.exe"],
            environmentVariable,
            command,
            "missing",
            startupDelay: TimeSpan.FromSeconds(4),
            captureDelay: TimeSpan.FromSeconds(3),
            timeout: TimeSpan.FromSeconds(15),
            maximumOutputCharacters: 4096,
            workingDirectory: workingDirectory,
            inputReadyMarker: inputReadyMarker,
            commandReadyMarker: commandReadyMarker,
            commandReadyTimeout: TimeSpan.FromSeconds(3));

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

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(_directory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateExecutable(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static void AssertPathEqual(string expected, string? actual) => Assert.Equal(
        Path.GetFullPath(expected),
        actual,
        ignoreCase: true);

    private static string CommandProcessorPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "cmd.exe");

    private static string PowerShellPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    private static string PowerShellLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
