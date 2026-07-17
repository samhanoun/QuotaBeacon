using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace QuotaBeacon.Core.Providers.Google;

internal sealed class WindowsPseudoConsoleSession : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint Infinite = 0xffffffff;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int StartfUseStdHandles = 0x00000100;
    private const nuint ProcThreadAttributePseudoConsole = 0x00020016;
    private readonly SafePseudoConsoleHandle _pseudoConsole;
    private readonly SafeJobHandle _job;
    private readonly SafeKernelHandle _process;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private int _stopped;

    private WindowsPseudoConsoleSession(
        SafePseudoConsoleHandle pseudoConsole,
        SafeJobHandle job,
        SafeKernelHandle process,
        SafeFileHandle input,
        SafeFileHandle output)
    {
        _pseudoConsole = pseudoConsole;
        _job = job;
        _process = process;
        _input = new FileStream(input, FileAccess.Write, bufferSize: 4096, isAsync: false);
        _output = new FileStream(output, FileAccess.Read, bufferSize: 4096, isAsync: false);
    }

    public Stream Output => _output;

    public static WindowsPseudoConsoleSession Start(string executable)
    {
        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        SafePseudoConsoleHandle? pseudoConsole = null;
        SafeJobHandle? job = null;
        SafeKernelHandle? process = null;
        SafeKernelHandle? thread = null;
        nint attributeList = 0;
        var attributeListInitialized = false;

        try
        {
            CreatePipePair(out inputRead, out inputWrite);
            CreatePipePair(out outputRead, out outputWrite);

            var result = NativeMethods.CreatePseudoConsole(
                new Coord { X = 120, Y = 40 },
                inputRead,
                outputWrite,
                0,
                out var pseudoConsoleRaw);
            if (result < 0)
            {
                throw new InvalidOperationException("Windows could not create an interactive pseudo-terminal.");
            }

            pseudoConsole = new SafePseudoConsoleHandle(pseudoConsoleRaw, ownsHandle: true);

            nuint attributeListSize = 0;
            _ = NativeMethods.InitializeProcThreadAttributeList(0, 1, 0, ref attributeListSize);
            if (attributeListSize == 0)
            {
                throw LastWin32Exception("Windows could not size the pseudo-terminal startup information.");
            }

            attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw LastWin32Exception("Windows could not initialize the pseudo-terminal startup information.");
            }

            attributeListInitialized = true;
            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributePseudoConsole,
                    pseudoConsole.DangerousGetHandle(),
                    (nuint)nint.Size,
                    0,
                    0))
            {
                throw LastWin32Exception("Windows could not attach the pseudo-terminal startup information.");
            }

            job = NativeMethods.CreateJobObject(0, null);
            if (job.IsInvalid)
            {
                throw LastWin32Exception("Windows could not create the CLI cleanup job.");
            }

            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            if (!NativeMethods.SetInformationJobObject(
                    job,
                    JobObjectInformationClass.ExtendedLimitInformation,
                    ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw LastWin32Exception("Windows could not configure the CLI cleanup job.");
            }

            var command = CreateCommand(executable);
            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles
                },
                AttributeList = attributeList
            };
            var processAttributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>()
            };
            var threadAttributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>()
            };
            var commandLine = new StringBuilder(command.CommandLine, command.CommandLine.Length + 1);
            if (!NativeMethods.CreateProcess(
                    command.ApplicationName,
                    commandLine,
                    ref processAttributes,
                    ref threadAttributes,
                    false,
                    CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                    0,
                    Path.GetTempPath(),
                    ref startupInfo,
                    out var processInformation))
            {
                throw LastWin32Exception("Windows could not start the official CLI.");
            }

            process = new SafeKernelHandle(processInformation.Process, ownsHandle: true);
            thread = new SafeKernelHandle(processInformation.Thread, ownsHandle: true);
            if (!NativeMethods.AssignProcessToJobObject(job, process))
            {
                var error = Marshal.GetLastWin32Error();
                _ = NativeMethods.TerminateProcess(process, 1);
                throw new Win32Exception(error, "Windows could not contain the official CLI process tree.");
            }

            if (NativeMethods.ResumeThread(thread) == Infinite)
            {
                var error = Marshal.GetLastWin32Error();
                _ = NativeMethods.TerminateJobObject(job, 1);
                throw new Win32Exception(error, "Windows could not resume the official CLI.");
            }

            thread.Dispose();
            thread = null;

            // ConPTY has retained its own references after CreateProcess. Releasing these host-side
            // duplicates is required for broken-channel detection during teardown.
            inputRead.Dispose();
            inputRead = null;
            outputWrite.Dispose();
            outputWrite = null;

            var session = new WindowsPseudoConsoleSession(
                pseudoConsole,
                job,
                process,
                inputWrite,
                outputRead);
            pseudoConsole = null;
            job = null;
            process = null;
            inputWrite = null;
            outputRead = null;
            return session;
        }
        finally
        {
            if (attributeListInitialized)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
            }

            if (attributeList != 0)
            {
                Marshal.FreeHGlobal(attributeList);
            }

            thread?.Dispose();
            process?.Dispose();
            job?.Dispose();
            pseudoConsole?.Dispose();
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
        }
    }

    public async ValueTask WriteLineAsync(string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(value, "\r"));
        await Task.Run(
            () =>
            {
                if (!NativeMethods.WriteFile(
                        _input.SafeFileHandle,
                        bytes,
                        (uint)bytes.Length,
                        out var written,
                        0) || written != bytes.Length)
                {
                    throw LastWin32Exception("Windows could not send the usage command to the official CLI.");
                }
            },
            cancellationToken);
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        // Kill-on-close job containment ensures descendants cannot survive a timeout or cancellation.
        _ = NativeMethods.TerminateJobObject(_job, 1);
        _ = NativeMethods.TerminateProcess(_process, 1);
        _ = NativeMethods.WaitForSingleObject(_process, 2_000);
        _input.Dispose();

        // Output stays open and is drained by the dedicated reader while ClosePseudoConsole emits
        // its final VT frame. This ordering avoids the documented ConPTY shutdown deadlock.
        _pseudoConsole.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _output.Dispose();
        _process.Dispose();
        _job.Dispose();
    }

    private static void CreatePipePair(out SafeFileHandle read, out SafeFileHandle write)
    {
        if (!NativeMethods.CreatePipe(out read, out write, 0, 0))
        {
            throw LastWin32Exception("Windows could not create a pseudo-terminal channel.");
        }
    }

    private static Command CreateCommand(string executable)
    {
        var extension = Path.GetExtension(executable);
        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return new Command(executable, QuoteArgument(executable));
        }

        var commandProcessor = GetCommandProcessor();
        return new Command(
            commandProcessor,
            $"{QuoteArgument(commandProcessor)} /d /s /c \"\"{executable}\"\"");
    }

    private static string GetCommandProcessor()
    {
        var configured = Environment.GetEnvironmentVariable("COMSPEC");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static Win32Exception LastWin32Exception(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    private sealed record Command(string ApplicationName, string CommandLine);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ProcessInformation
    {
        public readonly nint Process;
        public readonly nint Thread;
        public readonly uint ProcessId;
        public readonly uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9
    }

    private sealed class SafePseudoConsoleHandle(nint handle, bool ownsHandle) : SafeHandle(handle, ownsHandle)
    {
        public override bool IsInvalid => handle == 0 || handle == -1;

        protected override bool ReleaseHandle()
        {
            NativeMethods.ClosePseudoConsole(handle);
            return true;
        }
    }

    private sealed class SafeKernelHandle(nint handle, bool ownsHandle) : SafeHandle(handle, ownsHandle)
    {
        public override bool IsInvalid => handle == 0 || handle == -1;

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private sealed class SafeJobHandle : SafeHandle
    {
        private SafeJobHandle() : base(0, ownsHandle: true)
        {
        }

        public override bool IsInvalid => handle == 0 || handle == -1;

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreatePipe(
            out SafeFileHandle readPipe,
            out SafeFileHandle writePipe,
            nint pipeAttributes,
            uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(
            SafeFileHandle file,
            byte[] buffer,
            uint bytesToWrite,
            out uint bytesWritten,
            nint overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(
            Coord size,
            SafeFileHandle input,
            SafeFileHandle output,
            uint flags,
            out nint pseudoConsole);

        [DllImport("kernel32.dll")]
        public static extern void ClosePseudoConsole(nint pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InitializeProcThreadAttributeList(
            nint attributeList,
            int attributeCount,
            uint flags,
            ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateProcThreadAttribute(
            nint attributeList,
            uint flags,
            nuint attribute,
            nint value,
            nuint size,
            nint previousValue,
            nint returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(nint attributeList);

        [SuppressMessage("Performance", "CA1838:Avoid StringBuilder parameters for P/Invokes", Justification = "CreateProcessW requires a mutable LPWSTR command line.")]
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcess(
            string? applicationName,
            [In, Out] StringBuilder commandLine,
            ref SecurityAttributes processAttributes,
            ref SecurityAttributes threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            nint environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeJobHandle CreateJobObject(nint jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(
            SafeJobHandle job,
            JobObjectInformationClass informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeKernelHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(SafeKernelHandle thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateProcess(SafeKernelHandle process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint handle);
    }
}
