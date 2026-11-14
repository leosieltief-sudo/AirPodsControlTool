using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AirPodsControl.Windows;

internal static partial class Native
{
    private const int CR_SUCCESS = 0;
    private const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
    private const int CM_GETIDLIST_FILTER_PRESENT = 0x100;
    private const int CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x0;

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out int len, string? filter, int flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, int len, int flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out int len, ref Guid cls, string? deviceId, int flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid cls, string? deviceId, [Out] char[] buffer, int len, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    internal const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

    /// <summary>Present device instance IDs below an enumerator (e.g. "BTHENUM"). Works without admin rights.</summary>
    public static IReadOnlyList<string> GetPresentDeviceIds(string enumerator)
    {
        const int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out int len, enumerator, flags) != CR_SUCCESS || len <= 1) return [];
            var buf = new char[len];
            int cr = CM_Get_Device_ID_ListW(enumerator, buf, len, flags);
            if (cr == CR_SUCCESS) return SplitMultiSz(buf);
            // CR_BUFFER_SMALL (0x1A): devices changed between calls, retry
        }
        return [];
    }

    /// <summary>Device interface paths of class <paramref name="cls"/>, optionally restricted to one device instance.</summary>
    public static IReadOnlyList<string> GetDeviceInterfaces(Guid cls, string? deviceInstanceId)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out int len, ref cls, deviceInstanceId, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || len <= 1)
                return [];
            var buf = new char[len];
            if (CM_Get_Device_Interface_ListW(ref cls, deviceInstanceId, buf, len, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) == CR_SUCCESS)
                return SplitMultiSz(buf);
        }
        return [];
    }

    private static List<string> SplitMultiSz(char[] buf) =>
        new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Name of the function driver bound to a device node (Enum key "Service" value), or null if none.</summary>
    public static string? GetBoundService(string instanceId)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId);
            return k?.GetValue("Service") as string;
        }
        catch { return null; }
    }

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);
    [DllImport("CfgMgr32.dll")] private static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);
    [DllImport("CfgMgr32.dll")] private static extern int CM_Get_Child(out uint child, uint devInst, int flags);
    [DllImport("CfgMgr32.dll")] private static extern int CM_Get_Sibling(out uint sibling, uint devInst, int flags);
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_IDW(uint devInst, [Out] char[] buffer, int len, int flags);

    private static string? IdOf(uint devInst)
    {
        var buf = new char[512];
        return CM_Get_Device_IDW(devInst, buf, buf.Length, 0) == CR_SUCCESS ? new string(buf).TrimEnd('\0') : null;
    }

    /// <summary>The node itself, its parent, grandparent … up to the root. Empty when the node is not present.</summary>
    public static IReadOnlyList<string> GetAncestorChain(string instanceId)
    {
        var chain = new List<string>();
        if (CM_Locate_DevNodeW(out uint cur, instanceId, 0) != CR_SUCCESS) return chain;
        for (int depth = 0; depth < 32; depth++)
        {
            if (IdOf(cur) is not { } id) break;
            chain.Add(id);
            if (CM_Get_Parent(out uint parent, cur, 0) != CR_SUCCESS) break;
            cur = parent;
        }
        return chain;
    }

    /// <summary>All present nodes below <paramref name="instanceId"/> (depth-first). Null when the node is not present.</summary>
    public static IReadOnlyList<string>? GetDescendants(string instanceId, int max = 2000)
    {
        if (CM_Locate_DevNodeW(out uint root, instanceId, 0) != CR_SUCCESS) return null;
        var result = new List<string>();
        var stack = new Stack<uint>();
        if (CM_Get_Child(out uint first, root, 0) == CR_SUCCESS) stack.Push(first);
        while (stack.Count > 0 && result.Count < max)
        {
            uint n = stack.Pop();
            if (IdOf(n) is { } id) result.Add(id);
            if (CM_Get_Sibling(out uint sib, n, 0) == CR_SUCCESS) stack.Push(sib);
            if (CM_Get_Child(out uint child, n, 0) == CR_SUCCESS) stack.Push(child);
        }
        return result;
    }

    public static bool IsServiceInstalled(string serviceName)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
            return k is not null;
        }
        catch { return false; }
    }
}
