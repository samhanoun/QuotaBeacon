using System.ComponentModel;
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
    TimeSpan? startupDelay = null) : ICliUsageSource
{
    private static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultCaptureDelay = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(14);
    private readonly int _maximumOutputCharacters =
        Math.Clamp(maximumOutputCharacters, 4 * 1024, 1024 * 1024);
    private readonly TimeSpan _startupDelay = PositiveOrDefault(startupDelay, DefaultStartupDelay);
    private readonly TimeSpan _captureDelay = PositiveOrDefault(captureDelay, DefaultCaptureDelay);
    private readonly TimeSpan _timeout = PositiveOrDefault(timeout, DefaultTimeout);

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
            session = WindowsPseudoConsoleSession.Start(executable);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            return Failed("The official CLI could not be started in an interactive terminal.");
        }

        using (session)
        using (var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutCancellation.CancelAfter(_timeout);
            var outputTask = ReadBoundedAsync(
                session.Output,
                _maximumOutputCharacters,
                CancellationToken.None);

            try
            {
                // Give the interactive CLI time to attach to the pseudo-terminal before input is queued.
                await Task.Delay(_startupDelay, timeoutCancellation.Token);
                await session.WriteLineAsync(slashCommand, timeoutCancellation.Token);
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
            catch (Exception exception) when (exception is Win32Exception or IOException or ObjectDisposedException)
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
        if (IsAllowedExecutable(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured!);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var rawDirectory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            {
                continue;
            }

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

    private static async Task<string> ReadBoundedAsync(
        Stream stream,
        int maximumCharacters,
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
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
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

    private static bool IsAllowedExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) &&
        Path.GetExtension(path) is { } extension &&
        (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".bat", StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        fileName == Path.GetFileName(fileName) &&
        IsAllowedExecutable(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, fileName));

    private static TimeSpan PositiveOrDefault(TimeSpan? value, TimeSpan fallback) =>
        value is { } candidate && candidate > TimeSpan.Zero ? candidate : fallback;

    private static CliUsageReadResult Failed(string diagnostic) =>
        new(CliUsageReadStatus.Failed, string.Empty, diagnostic);
}
