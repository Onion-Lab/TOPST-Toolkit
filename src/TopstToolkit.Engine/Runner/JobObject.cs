using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TopstToolkit.Engine.Runner;

/// <summary>
/// A Windows Job Object with KILL_ON_JOB_CLOSE: closing it (or the app dying) ends the whole process tree.
/// Also reports the peak committed memory of the processes it held.
/// Windows only: <see cref="ToolRunner"/> creates it behind an <c>OperatingSystem.IsWindows()</c> check.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private readonly SafeJobHandle _handle;

    public JobObject()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject failed");
        }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
    }

    public bool Terminate(uint exitCode) => TerminateJobObject(_handle, exitCode);

    /// <summary>Peak committed (private) bytes of any single process ever in the job.</summary>
    public long QueryPeakProcessMemory()
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        return QueryInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info,
                   (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(), IntPtr.Zero)
            ? (long)info.PeakProcessMemoryUsed.ToUInt64()
            : 0;
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeJobHandle hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle hJob, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
