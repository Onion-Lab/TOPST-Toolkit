using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TopstToolkit.Engine.Runner;

/// <summary>
/// Keeps Windows from idle-sleeping while a tool runs (PowerCreateRequest + PowerRequestSystemRequired).
/// The request is listed by <c>powercfg /requests</c> with the given reason and disappears on Dispose.
/// It does not block a sleep that is asked for (Start menu, lid, power button, another program): that suspends
/// the tool, which simply continues after wake-up. Windows only; created behind <c>OperatingSystem.IsWindows()</c>.
/// </summary>
internal sealed class PowerRequest : IDisposable
{
    private const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;
    private const int PowerRequestSystemRequired = 1;

    private readonly SafePowerRequestHandle _handle;

    public PowerRequest(string reason)
    {
        var context = new REASON_CONTEXT
        {
            Version = POWER_REQUEST_CONTEXT_VERSION,
            Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            SimpleReasonString = reason,
        };
        _handle = PowerCreateRequest(ref context);
        IsActive = !_handle.IsInvalid && PowerSetRequest(_handle, PowerRequestSystemRequired);
    }

    /// <summary>False when Windows refused the request; the run goes on without it.</summary>
    public bool IsActive { get; }

    public void Dispose()
    {
        if (IsActive)
            PowerClearRequest(_handle, PowerRequestSystemRequired);
        _handle.Dispose();
    }

    // With POWER_REQUEST_CONTEXT_SIMPLE_STRING only the string pointer of the union is read.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    private sealed class SafePowerRequestHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePowerRequestHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafePowerRequestHandle PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafePowerRequestHandle handle, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafePowerRequestHandle handle, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
