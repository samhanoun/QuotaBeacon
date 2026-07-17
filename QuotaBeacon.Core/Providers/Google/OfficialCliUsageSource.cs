using System.ComponentModel;
using System.Security;
using System.Text;

namespace QuotaBeacon.Core.Providers.Google;

public sealed class OfficialCliUsageSource(
    IReadOnlyList<string> executableNames,
    string environmentOverride,
    string slashCommand,
    string missingDiagnostic,
    TimeSpan? captureDelay = null,
    TimeSpan? timeout = null,
    int maximumOutputCharacters = 256 * 1024,
    TimeSpan? startupDelay = null,
    string? workingDirectory = null,
    IReadOnlyList<string>? officialDirectories = null,
    string? inputReadyMarker = null,
    string? commandReadyMarker = null,
    IReadOnlyList<string>? inputBlockedMarkers = null,
    TimeSpan? commandReadyTimeout = null) : ICliUsageSource
{
    private static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultCaptureDelay = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(14);
    private static readonly TimeSpan DefaultCommandReadyTimeout = TimeSpan.FromSeconds(20);
    private readonly int _maximumOutputCharacters =
        Math.Clamp(maximumOutputCharacters, 4 * 1024, 1024 * 1024);
    private readonly TimeSpan _startupDelay = PositiveOrDefault(startupDelay, DefaultStartupDelay);
    private readonly TimeSpan _captureDelay = PositiveOrDefault(captureDelay, DefaultCaptureDelay);
    private readonly TimeSpan _timeout = PositiveOrDefault(timeout, DefaultTimeout);
    private readonly TimeSpan _commandReadyTimeout =
        PositiveOrDefault(commandReadyTimeout, DefaultCommandReadyTimeout);

    public async Task<CliUsageReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = LocateExecutable();
        if (executable is null)
        {
            return new CliUsageReadResult(
                CliUsageReadStatus.NotInstalled,
                string.Empty,
                missingDiagnostic);
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return Failed("The official CLI requires Windows 10 version 1809 or newer for secure interactive capture.");
        }

        WindowsPseudoConsoleSession session;
        try
        {
            session = WindowsPseudoConsoleSession.Start(executable, ResolveWorkingDirectory());
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            return Failed("The official CLI could not be started in an interactive terminal.");
        }

        using (session)
        using (var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutCancellation.CancelAfter(_timeout);
            var inputReadySignal = string.IsNullOrWhiteSpace(inputReadyMarker)
                ? null
                : new TaskCompletionSource<InputReadiness>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var commandReadySignal = string.IsNullOrWhiteSpace(commandReadyMarker)
                ? null
                : new TaskCompletionSource<InputReadiness>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var blockedMarkers = inputBlockedMarkers ?? [];
            var blockedSignal = blockedMarkers.Count == 0
                ? null
                : new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var commandReadinessGate = commandReadySignal is null
                ? null
                : new CommandReadinessGate();
            var outputTask = ReadBoundedAsync(
                session.Output,
                _maximumOutputCharacters,
                inputReadyMarker,
                inputReadySignal,
                commandReadyMarker,
                blockedMarkers,
                commandReadySignal,
                commandReadinessGate,
                blockedSignal,
                CancellationToken.None);

            try
            {
                // Give the interactive CLI time to attach to the pseudo-terminal before input is queued.
                await Task.Delay(_startupDelay, timeoutCancellation.Token);
                if (inputReadySignal is not null)
                {
                    InputReadiness inputReadiness;
                    try
                    {
                        inputReadiness = await inputReadySignal.Task.WaitAsync(
                            _commandReadyTimeout,
                            timeoutCancellation.Token);
                    }
                    catch (TimeoutException)
                    {
                        session.Stop();
                        return new CliUsageReadResult(
                            CliUsageReadStatus.Failed,
                            await CompleteReadAsync(outputTask),
                            "The official CLI input prompt did not become ready; no command was submitted.");
                    }

                    if (inputReadiness == InputReadiness.Blocked)
                    {
                        session.Stop();
                        return new CliUsageReadResult(
                            CliUsageReadStatus.Available,
                            await CompleteReadAsync(outputTask),
                            null);
                    }

                    if (inputReadiness != InputReadiness.Ready)
                    {
                        session.Stop();
                        return new CliUsageReadResult(
                            CliUsageReadStatus.Failed,
                            await CompleteReadAsync(outputTask),
                            "The official CLI ended before its input prompt became ready; no command was submitted.");
                    }
                }

                if (commandReadySignal is null)
                {
                    await session.WriteLineAsync(slashCommand, timeoutCancellation.Token);
                }
                else
                {
                    if (slashCommand.Length > 1)
                    {
                        await session.TypeLineAsync(slashCommand[..^1], timeoutCancellation.Token);
                        await Task.Delay(TimeSpan.FromMilliseconds(75), timeoutCancellation.Token);
                    }

                    if (IsBlocked(blockedSignal))
                    {
                        session.Stop();
                        return new CliUsageReadResult(
                            CliUsageReadStatus.Available,
                            await CompleteReadAsync(outputTask),
                            null);
                    }

                    // Ignore menu text that was already present before the final command
                    // keystroke. Only a fresh redraw may authorize Enter.
                    commandReadinessGate!.Arm();

                    // Typing is harmless on its own; Enter is withheld until the CLI renders
                    // the exact command-menu marker configured for this provider.
                    await session.TypeLineAsync(slashCommand[^1..], timeoutCancellation.Token);
                    var readiness = commandReadySignal.Task.IsCompletedSuccessfully
                        ? commandReadySignal.Task.Result
                        : InputReadiness.Pending;
                    if (readiness != InputReadiness.Ready)
                    {
                        try
                        {
                            readiness = await WaitForCommandReadinessAsync(
                                commandReadySignal,
                                blockedSignal,
                                _commandReadyTimeout,
                                timeoutCancellation.Token);
                        }
                        catch (TimeoutException)
                        {
                            session.Stop();
                            return new CliUsageReadResult(
                                CliUsageReadStatus.Failed,
                                await CompleteReadAsync(outputTask),
                                "The official CLI command menu did not become ready; no command was submitted.");
                        }
                    }

                    if (readiness == InputReadiness.Blocked)
                    {
                        session.Stop();
                        return new CliUsageReadResult(
                            CliUsageReadStatus.Available,
                            await CompleteReadAsync(outputTask),
                            null);
                    }

                    if (readiness != InputReadiness.Ready)
                    {
                        session.Stop();
                        return new CliUsageReadResult(
                            CliUsageReadStatus.Failed,
                            await CompleteReadAsync(outputTask),
                            "The official CLI ended before its command menu became ready; no command was submitted.");
                    }

                    // Keep observing trust/authentication markers after autocomplete. A late
                    // modal prompt must never receive the Enter intended for the slash command.
                    await Task.Delay(TimeSpan.FromMilliseconds(150), timeoutCancellation.Token);
                    if (IsBlocked(blockedSignal))
                    {
                        session.Stop();
                        return new CliUsageReadResult(
                            CliUsageReadStatus.Available,
                            await CompleteReadAsync(outputTask),
                            null);
                    }

                    await session.SendEnterAsync(timeoutCancellation.Token);
                }

                await Task.Delay(_captureDelay, timeoutCancellation.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                session.Stop();
                await CompleteReadAsync(outputTask);
                throw;
            }
            catch (OperationCanceledException)
            {
                session.Stop();
                var timedOutOutput = await CompleteReadAsync(outputTask);
                return new CliUsageReadResult(
                    CliUsageReadStatus.Failed,
                    timedOutOutput,
                    "The official CLI did not return usage data in time.");
            }
            catch (Exception exception) when (exception is Win32Exception or IOException or ObjectDisposedException or ArgumentException)
            {
                session.Stop();
                return new CliUsageReadResult(
                    CliUsageReadStatus.Failed,
                    await CompleteReadAsync(outputTask),
                    "The official CLI terminal session ended unexpectedly.");
            }

            // Never send /quit, /exit, Escape, or other text that could fall through to a model prompt.
            // Closing the job and pseudo-console is the only shutdown path after the usage command.
            session.Stop();
            return new CliUsageReadResult(
                CliUsageReadStatus.Available,
                await CompleteReadAsync(outputTask),
                null);
        }
    }

    private string? LocateExecutable()
    {
        var configured = Environment.GetEnvironmentVariable(environmentOverride);
        return FindExecutable(
            executableNames,
            configured,
            ReadEnvironmentPath(EnvironmentVariableTarget.Process),
            ReadEnvironmentPath(EnvironmentVariableTarget.User),
            ReadEnvironmentPath(EnvironmentVariableTarget.Machine),
            officialDirectories ?? []);
    }

    internal static string? FindExecutable(
        IReadOnlyList<string> executableNames,
        string? configuredExecutable,
        string? processPath,
        string? userPath,
        string? machinePath,
        IReadOnlyList<string> officialDirectories)
    {
        ArgumentNullException.ThrowIfNull(executableNames);
        ArgumentNullException.ThrowIfNull(officialDirectories);

        var configured = ExistingAllowedExecutable(configuredExecutable);
        if (configured is not null)
        {
            return configured;
        }

        foreach (var directory in EnumeratePathDirectories(processPath, userPath, machinePath, officialDirectories))
        {
            foreach (var executableName in executableNames)
            {
                if (!IsSafeFileName(executableName))
                {
                    continue;
                }

                try
                {
                    var candidate = Path.Combine(directory, executableName);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // Ignore malformed or inaccessible PATH entries.
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumeratePathDirectories(
        string? processPath,
        string? userPath,
        string? machinePath,
        IReadOnlyList<string> officialDirectories)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var officialDirectory in officialDirectories)
        {
            if (TryNormalizeDirectory(officialDirectory, out var directory) && seen.Add(directory))
            {
                yield return directory;
            }
        }

        foreach (var pathValue in new[] { processPath, userPath, machinePath })
        {
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                continue;
            }

            foreach (var rawDirectory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryNormalizeDirectory(rawDirectory.Trim().Trim('"'), out var directory) &&
                    seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }
    }

    private static string? ReadEnvironmentPath(EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariable("PATH", target);
        }
        catch (Exception exception) when (exception is SecurityException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static bool TryNormalizeDirectory(string? value, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value);
            if (!Path.IsPathFullyQualified(expanded))
            {
                return false;
            }

            directory = Path.GetFullPath(expanded);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private string ResolveWorkingDirectory()
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Path.GetFullPath(Path.GetTempPath());
        }

        if (!TryNormalizeDirectory(workingDirectory, out var configured) || !Directory.Exists(configured))
        {
            throw new InvalidOperationException("The official CLI working directory is unavailable.");
        }

        return configured;
    }

    private static bool IsBlocked(TaskCompletionSource<bool>? blockedSignal) =>
        blockedSignal?.Task.IsCompletedSuccessfully == true;

    private static async Task<InputReadiness> WaitForCommandReadinessAsync(
        TaskCompletionSource<InputReadiness> readinessSignal,
        TaskCompletionSource<bool>? blockedSignal,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (IsBlocked(blockedSignal))
        {
            return InputReadiness.Blocked;
        }

        if (blockedSignal is null)
        {
            return await readinessSignal.Task.WaitAsync(timeout, cancellationToken);
        }

        _ = await Task.WhenAny(readinessSignal.Task, blockedSignal.Task)
            .WaitAsync(timeout, cancellationToken);
        return IsBlocked(blockedSignal)
            ? InputReadiness.Blocked
            : await readinessSignal.Task;
    }

    private static async Task<string> ReadBoundedAsync(
        Stream stream,
        int maximumCharacters,
        string? inputReadyMarker,
        TaskCompletionSource<InputReadiness>? inputReadySignal,
        string? readyMarker,
        IReadOnlyList<string> blockedMarkers,
        TaskCompletionSource<InputReadiness>? readinessSignal,
        CommandReadinessGate? commandReadinessGate,
        TaskCompletionSource<bool>? blockedSignal,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        var builder = new StringBuilder(Math.Min(maximumCharacters, 16 * 1024));
        var buffer = new char[4096];
        var markerTail = string.Empty;
        var commandMarkerTail = string.Empty;
        var markerWindowLength = Math.Max(
            512,
            Math.Max(
                Math.Max(inputReadyMarker?.Length ?? 0, readyMarker?.Length ?? 0),
                blockedMarkers.Count == 0 ? 0 : blockedMarkers.Max(marker => marker?.Length ?? 0)) * 2);
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                inputReadySignal?.TrySetResult(InputReadiness.Ended);
                readinessSignal?.TrySetResult(InputReadiness.Ended);
                break;
            }

            if ((inputReadySignal is not null && !inputReadySignal.Task.IsCompleted) ||
                (blockedSignal is not null && !blockedSignal.Task.IsCompleted))
            {
                var markerWindow = string.Concat(markerTail, new string(buffer, 0, read));
                var blocked = blockedMarkers.Any(marker =>
                    !string.IsNullOrWhiteSpace(marker) &&
                    markerWindow.Contains(marker, StringComparison.OrdinalIgnoreCase));
                if (blocked)
                {
                    blockedSignal?.TrySetResult(true);
                    inputReadySignal?.TrySetResult(InputReadiness.Blocked);
                    readinessSignal?.TrySetResult(InputReadiness.Blocked);
                }
                else
                {
                    if (inputReadySignal is not null &&
                        !string.IsNullOrWhiteSpace(inputReadyMarker) &&
                        markerWindow.Contains(inputReadyMarker, StringComparison.OrdinalIgnoreCase))
                    {
                        inputReadySignal.TrySetResult(InputReadiness.Ready);
                    }
                }

                markerTail = markerWindow[^Math.Min(markerWindow.Length, markerWindowLength)..];
            }

            if (readinessSignal is not null &&
                !readinessSignal.Task.IsCompleted &&
                commandReadinessGate?.IsArmed == true)
            {
                var commandMarkerWindow = string.Concat(
                    commandMarkerTail,
                    new string(buffer, 0, read));
                if (!string.IsNullOrWhiteSpace(readyMarker) &&
                    commandMarkerWindow.Contains(readyMarker, StringComparison.OrdinalIgnoreCase))
                {
                    readinessSignal.TrySetResult(InputReadiness.Ready);
                }

                commandMarkerTail = commandMarkerWindow[
                    ^Math.Min(commandMarkerWindow.Length, markerWindowLength)..];
            }
            else if (commandReadinessGate?.IsArmed != true)
            {
                commandMarkerTail = string.Empty;
            }

            // Keep draining after the retained-output cap so a noisy synchronous ConPTY client
            // cannot backpressure teardown. Only the returned text is bounded.
            var retained = Math.Min(read, maximumCharacters - builder.Length);
            if (retained > 0)
            {
                builder.Append(buffer, 0, retained);
            }
        }

        return builder.ToString();
    }

    private static async Task<string> CompleteReadAsync(Task<string> readTask)
    {
        try
        {
            return await readTask;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    private static string? ExistingAllowedExecutable(string? path)
    {
        if (!IsAllowedExecutable(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path!);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsAllowedExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return Path.IsPathFullyQualified(path) && IsAllowedExtension(Path.GetExtension(path));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        try
        {
            return fileName == Path.GetFileName(fileName) &&
                   IsAllowedExtension(Path.GetExtension(fileName));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsAllowedExtension(string extension) =>
        extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan PositiveOrDefault(TimeSpan? value, TimeSpan fallback) =>
        value is { } candidate && candidate > TimeSpan.Zero ? candidate : fallback;

    private static CliUsageReadResult Failed(string diagnostic) =>
        new(CliUsageReadStatus.Failed, string.Empty, diagnostic);

    private enum InputReadiness
    {
        Pending,
        Ready,
        Blocked,
        Ended
    }

    private sealed class CommandReadinessGate
    {
        private int _armed;

        public bool IsArmed => Volatile.Read(ref _armed) != 0;

        public void Arm() => Volatile.Write(ref _armed, 1);
    }
}
