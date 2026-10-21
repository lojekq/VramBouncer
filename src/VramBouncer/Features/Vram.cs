using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using VramBouncer.Shell;

namespace VramBouncer.Features;

public readonly record struct GpuMemory(string Name, long Total, long Used, long Free);

/// <summary>
/// Totals from NVML (decisions are taken on these), per-process dedicated VRAM from PDH (only for our pids: dwm and overlays
/// read far above the physical size there, spec §1). Without NVML the total is hidden and restore waits for the grace only.
/// </summary>
public static partial class VramMonitor
{
    static nint device;
    static string name = "";
    static bool? nvmlOk;

    /// <summary>The NVIDIA device with the most memory; null without NVML (AMD, Intel, broken driver).</summary>
    public static GpuMemory? Read()
    {
        try
        {
            if (nvmlOk ??= InitNvml()) // ponytail: one device for the whole run; a settings choice when someone has two big GPUs
            {
                var m = new NvmlMemoryV2 { Version = (uint)Marshal.SizeOf<NvmlMemoryV2>() | (2u << 24) };
                if (nvmlDeviceGetMemoryInfo_v2(device, ref m) == 0) return new(name, (long)m.Total, (long)m.Used, (long)m.Free);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { nvmlOk = false; }
        return null;
    }

    static bool InitNvml()
    {
        try
        {
            if (nvmlInit_v2() != 0 || nvmlDeviceGetCount_v2(out var n) != 0) return false;
            ulong best = 0;
            for (uint i = 0; i < n; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out var h) != 0) continue;
                var m = new NvmlMemoryV2 { Version = (uint)Marshal.SizeOf<NvmlMemoryV2>() | (2u << 24) };
                if (nvmlDeviceGetMemoryInfo_v2(h, ref m) != 0 || m.Total <= best) continue;
                best = m.Total;
                device = h;
                var buf = new byte[96];
                name = nvmlDeviceGetName(h, buf, (uint)buf.Length) == 0 ? System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0') : "GPU";
            }
            return best > 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Info("no NVML: VRAM totals hidden");
            return false;
        }
    }

    /// <summary>
    /// Dedicated VRAM of the given pids, summed over all adapters (one ReadCategory call), each capped at the total.
    /// Empty when the counter category is missing or disabled.
    /// </summary>
    public static Dictionary<int, long> PerProcess(IReadOnlyCollection<int> pids, long cap)
    {
        var r = new Dictionary<int, long>();
        if (pids.Count == 0) return r;
        try
        {
            var data = new PerformanceCounterCategory("GPU Process Memory").ReadCategory()["Dedicated Usage"];
            if (data is null) return r;
            foreach (InstanceData d in data.Values)
                if (PidOf(d.InstanceName) is { } pid && pids.Contains(pid))
                    r[pid] = r.GetValueOrDefault(pid) + d.RawValue;
            if (cap > 0) foreach (var k in r.Keys) r[k] = Math.Min(r[k], cap);
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        return r;
    }

    internal static int? PidOf(string instance) => PidRegex().Match(instance) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    [GeneratedRegex(@"^pid_(\d+)_")] private static partial Regex PidRegex();

    [StructLayout(LayoutKind.Sequential)]
    struct NvmlMemoryV2 { public uint Version; public ulong Total, Reserved, Free, Used; }

    [LibraryImport("nvml.dll")] private static partial int nvmlInit_v2();
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetCount_v2(out uint count);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetMemoryInfo_v2(nint device, ref NvmlMemoryV2 memory);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetName(nint device, [Out] byte[] name, uint length);
}

/// <summary>Who listens on a TCP port (IPv4 and IPv6), via GetExtendedTcpTable.</summary>
public static partial class TcpPorts
{
    public static int? ListenerPid(int port) => Listener(port, 2, 24, 8, 20) ?? Listener(port, 23, 56, 20, 52);

    // rowSize, portOffset, pidOffset: MIB_TCPROW_OWNER_PID (v4) and MIB_TCP6ROW_OWNER_PID (v6)
    static int? Listener(int port, uint af, int rowSize, int portOffset, int pidOffset)
    {
        uint size = 0;
        GetExtendedTcpTable(null, ref size, false, af, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buf = new byte[size + 1024];
            size = (uint)buf.Length;
            var err = GetExtendedTcpTable(buf, ref size, false, af, 3, 0);
            if (err == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the table grew
            if (err != 0) return null;
            var n = BinaryPrimitives.ReadInt32LittleEndian(buf);
            for (var i = 0; i < n; i++)
            {
                var row = 4 + i * rowSize;
                var p = (buf[row + portOffset] << 8) | buf[row + portOffset + 1]; // network byte order in the low word
                if (p == port) return BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(row + pidOffset));
            }
            return null;
        }
        return null;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable([Out] byte[]? table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint af, int tableClass, uint reserved);
}
