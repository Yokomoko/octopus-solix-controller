using System.Runtime.InteropServices;

namespace Solix.Worker;

/// <summary>Holds a Windows "system required" power request so idle timeouts don't put the PC into standby. No-op elsewhere.</summary>
public sealed class KeepAwake(ILogger logger) : IDisposable
{
    private IntPtr _request;
    private bool _held;

    public void Hold(bool hold)
    {
        if (!OperatingSystem.IsWindows() || hold == _held) return;
        try
        {
            if (_request == IntPtr.Zero)
            {
                var reason = new ReasonContext { Version = 0, Flags = SimpleString, Reason = "Solix Load Follower is controlling the battery" };
                _request = PowerCreateRequest(ref reason);
                if (_request == IntPtr.Zero || _request == new IntPtr(-1))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            var ok = hold ? PowerSetRequest(_request, SystemRequired) : PowerClearRequest(_request, SystemRequired);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _held = hold;
            logger.LogInformation(hold ? "Keeping Windows out of standby while Live" : "Letting Windows sleep again");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not change the keep-awake request");
        }
    }

    public void Dispose()
    {
        Hold(false);
        if (_request != IntPtr.Zero) CloseHandle(_request);
        _request = IntPtr.Zero;
    }

    private const uint SimpleString = 0x1;
    private const int SystemRequired = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string Reason;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
