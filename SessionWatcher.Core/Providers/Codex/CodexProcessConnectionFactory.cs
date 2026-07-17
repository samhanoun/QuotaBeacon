using System.Diagnostics;

namespace SessionWatcher.Core.Providers.Codex;

public sealed class CodexProcessConnectionFactory(string? executablePath = null)
    : ICodexAppServerConnectionFactory
{
    public Task<ICodexAppServerConnection> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedPath = ResolveExecutable(executablePath) ??
                           throw new FileNotFoundException("Codex CLI was not found.");
        var startInfo = CreateStartInfo(resolvedPath);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Codex app-server could not be started.");
            }

            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            return Task.FromResult<ICodexAppServerConnection>(new ProcessConnection(process));
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string path)
    {
        var extension = Path.GetExtension(path);
        ProcessStartInfo startInfo;

        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = BaseStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"\"{path}\" app-server");
        }
        else
        {
            startInfo = BaseStartInfo(path);
            startInfo.ArgumentList.Add("app-server");
        }

        return startInfo;
    }

    private static ProcessStartInfo BaseStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding = System.Text.Encoding.UTF8
    };

    private static string? ResolveExecutable(string? configuredPath)
    {
        var candidates = new[]
        {
            configuredPath,
            Environment.GetEnvironmentVariable("QUOTABEACON_CODEX_PATH"),
            Environment.GetEnvironmentVariable("SESSIONWATCHER_CODEX_PATH"),
            Environment.GetEnvironmentVariable("CODEX_CLI_PATH")
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var fileName in new[] { "codex.exe", "codex.cmd", "codex.bat" })
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), fileName);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }

    private sealed class ProcessConnection(Process process) : ICodexAppServerConnection
    {
        public async Task WriteLineAsync(string message, CancellationToken cancellationToken)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("Codex app-server exited unexpectedly.");
            }

            await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

        public async ValueTask DisposeAsync()
        {
            try
            {
                process.StandardInput.Close();
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                // The process already exited or could not be stopped; disposal still continues.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
