using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace StartupManager.Core;

// Only the Windows features without a built-in .NET API need P/Invoke.
internal static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint length, IntPtr buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackagePathByFullName(string name, ref uint length, StringBuilder? path);
    public static List<string> PackageNames(string family)
    {
        uint count = 0, length = 0;
        int error = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
        if (error != 122 || count == 0)
        {
            return [];
        }

        IntPtr names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)length * 2));
        try
        {
            if (GetPackagesByPackageFamily(family, ref count, names, ref length, buffer) != 0)
            {
                return [];
            }

            var result = new List<string>();
            for (int i = 0; i < count; i++)
            {
                result.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size))!);
            }

            return result;
        }
        finally { Marshal.FreeHGlobal(names); Marshal.FreeHGlobal(buffer); }
    }
    public static string? PackagePath(string name)
    {
        uint length = 0;
        if (GetPackagePathByFullName(name, ref length, null) != 122)
        {
            return null;
        }

        var buffer = new StringBuilder(checked((int)length));
        return GetPackagePathByFullName(name, ref length, buffer) == 0 ? buffer.ToString() : null;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")]
    static extern bool CloseServiceHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatus
    {
        public uint Type, State, Accepted, ExitCode, SpecificExitCode, Checkpoint, WaitHint;
    }
    public static bool ServiceRunning(string name)
    {
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            IntPtr service = OpenService(manager, name, 4);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return QueryServiceStatus(service, out var status) && status.State == 4;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FailureActions
    {
        public uint ResetPeriod; public IntPtr RebootMessage, Command; public uint Count; public IntPtr Actions;
    }
    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    static extern bool ChangeServiceConfig2(IntPtr service, uint level, ref FailureActions actions);
    public static void ClearServiceFailureActions(string name)
    {
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            IntPtr service = OpenService(manager, name, 2);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            // A non-null actions pointer with a zero count removes existing recovery actions.
            IntPtr empty = Marshal.AllocHGlobal(8);
            try
            {
                var actions = new FailureActions { Actions = empty };
                if (!ChangeServiceConfig2(service, 2, ref actions))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally { Marshal.FreeHGlobal(empty); CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
    public static bool PortListening(int port, HashSet<int> processIds)
    {
        foreach (int family in new[] { 2, 23 }) // IPv4 and IPv6, owner-PID listener tables.
        {
            int size = 0;
            uint error = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (error != 122)
            {
                if (error == 0)
                {
                    continue;
                }

                throw new Win32Exception((int)error);
            }
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                error = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                if (error != 0)
                {
                    throw new Win32Exception((int)error);
                }

                int count = Marshal.ReadInt32(buffer), rowSize = family == 2 ? 24 : 56;
                for (int i = 0; i < count; i++)
                {
                    int offset = 4 + i * rowSize, portOffset = offset + (family == 2 ? 8 : 20);
                    int localPort = (Marshal.ReadByte(buffer, portOffset) << 8) | Marshal.ReadByte(buffer, portOffset + 1);
                    int owner = Marshal.ReadInt32(buffer, offset + (family == 2 ? 20 : 52));
                    if (localPort == port && (owner == 4 || processIds.Contains(owner)))
                    {
                        return true;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return false;
    }
}
