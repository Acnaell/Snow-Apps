using System.Runtime.InteropServices;
using Snow.Core.Monitor;
using Snow.Platform.Native;

namespace Snow.Platform.Monitor;

/// <summary>
/// Uso de hardware pelos contadores de desempenho do Windows (PDH), a mesma base do Gerenciador de Tarefas.
/// Sem drivers e sem tocar em processos: compatível com anti-cheats.
/// </summary>
public sealed class PdhSampler : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr szName; public PDH_FMT_COUNTERVALUE FmtValue; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] internal static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] internal static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] internal static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] internal static extern uint PdhCloseQuery(IntPtr query);

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    private readonly IntPtr _query;
    private readonly IntPtr _cpuTotal, _cpuCores, _gpuEngines, _gpuMemory, _perf;

    /// <summary>
    /// "% Processor Performance" da última amostra: clock efetivo em % do clock base (acima de 100 = turbo).
    /// Queda durante um teste de estresse = processador reduzindo o clock (temperatura ou limite de energia).
    /// </summary>
    public double? LastPerformancePercent { get; private set; }

    public bool HasGpuCounters => _gpuEngines != IntPtr.Zero;

    public PdhSampler()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) throw new InvalidOperationException("PDH indisponível.");
        _cpuTotal = Add(@"\Processor Information(_Total)\% Processor Utility") is var t && t != IntPtr.Zero ? t : Add(@"\Processor(_Total)\% Processor Time");
        _cpuCores = Add(@"\Processor Information(*)\% Processor Utility") is var c && c != IntPtr.Zero ? c : Add(@"\Processor(*)\% Processor Time");
        _gpuEngines = Add(@"\GPU Engine(*engtype_3D)\Utilization Percentage");
        _gpuMemory = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _perf = Add(@"\Processor Information(_Total)\% Processor Performance");
        PdhCollectQueryData(_query); // primeira coleta: contadores de taxa precisam de duas leituras
    }

    private IntPtr Add(string path) => PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    private static double? Single(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        return PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) == 0 && v.CStatus == 0 ? v.doubleValue : null;
    }

    internal static List<(string name, double value)> Array(IntPtr counter)
    {
        var list = new List<(string, double)>();
        if (counter == IntPtr.Zero) return list;
        uint size = 0;
        var rc = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
        if (rc != PDH_MORE_DATA || size == 0) return list;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, buf) != 0) return list;
            var stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * stride);
                if (item.FmtValue.CStatus != 0) continue;
                list.Add((Marshal.PtrToStringUni(item.szName) ?? "", item.FmtValue.doubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    /// <summary>Coleta uma amostra (chame ~1x por segundo).</summary>
    public UsageSample Sample(double? fps = null, double? low1 = null)
    {
        PdhCollectQueryData(_query);
        LastPerformancePercent = _perf == IntPtr.Zero ? null : Single(_perf);
        var cpu = Single(_cpuTotal);
        var cores = Array(_cpuCores).Where(c => !c.name.Contains("_Total", StringComparison.OrdinalIgnoreCase)).Select(c => c.value).ToList();

        // GPU: soma dos motores 3D por adaptador (instância contém "luid_0x..._0x..."), máximo entre adaptadores
        double? gpu = null;
        var engines = Array(_gpuEngines);
        if (_gpuEngines != IntPtr.Zero)
        {
            gpu = engines.GroupBy(e => LuidOf(e.name)).Select(g => Math.Min(100, g.Sum(x => x.value))).DefaultIfEmpty(0).Max();
        }
        double? vram = null;
        var mem = Array(_gpuMemory);
        if (mem.Count > 0) vram = Math.Round(mem.Max(m => m.value) / 1024 / 1024 / 1024, 2);

        var ms = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        NativeMethods.GlobalMemoryStatusEx(ref ms);

        return new UsageSample(DateTime.Now,
            cpu is { } c0 ? Math.Round(Math.Min(100, c0), 1) : null,
            cores.Count > 0 ? Math.Round(Math.Min(100, cores.Max()), 1) : null,
            gpu is { } g0 ? Math.Round(g0, 1) : null,
            vram,
            ms.dwMemoryLoad,
            fps, low1);
    }

    private static string LuidOf(string instance)
    {
        var i = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return instance;
        var end = instance.IndexOf("_phys", i, StringComparison.OrdinalIgnoreCase);
        return end > i ? instance[i..end] : instance[i..];
    }

    public void Dispose() => PdhCloseQuery(_query);
}
