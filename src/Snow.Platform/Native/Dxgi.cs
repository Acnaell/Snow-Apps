using System.Runtime.InteropServices;

namespace Snow.Platform.Native;

/// <summary>Placa de vídeo como o DirectX a enxerga (mesma fonte do Gerenciador de Tarefas).</summary>
public sealed record DxgiAdapter(string Description, uint VendorId, uint DeviceId, ulong DedicatedVideoMemory, bool IsSoftware);

/// <summary>
/// Lê a VRAM dedicada via DXGI (IDXGIFactory1 → EnumAdapters1 → GetDesc1).
/// Necessário porque Win32_VideoController.AdapterRAM é um campo de 32 bits (máximo de 4 GB) e
/// alguns drivers não gravam HardwareInformation.qwMemorySize no registro.
/// </summary>
public static unsafe class Dxgi
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    private static Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    private const int VtRelease = 2;          // IUnknown::Release
    private const int VtEnumAdapters1 = 12;   // IDXGIFactory1::EnumAdapters1
    private const int VtGetDesc1 = 10;        // IDXGIAdapter1::GetDesc1
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    private static void Release(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        var vt = *(IntPtr**)p;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vt[VtRelease])(p);
    }

    public static IReadOnlyList<DxgiAdapter> Enumerate()
    {
        var list = new List<DxgiAdapter>();
        IntPtr factory;
        try
        {
            if (CreateDXGIFactory1(ref IID_IDXGIFactory1, out factory) != 0) return list;
        }
        catch (DllNotFoundException) { return list; }
        catch (EntryPointNotFoundException) { return list; }

        try
        {
            var fvt = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)fvt[VtEnumAdapters1];
            for (uint i = 0; i < 16; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) != 0) break; // DXGI_ERROR_NOT_FOUND: fim da lista
                try
                {
                    var avt = *(IntPtr**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC1*, int>)avt[VtGetDesc1];
                    DXGI_ADAPTER_DESC1 d;
                    if (getDesc1(adapter, &d) != 0) continue;
                    var name = new string(d.Description).TrimEnd('\0');
                    list.Add(new DxgiAdapter(name, d.VendorId, d.DeviceId, d.DedicatedVideoMemory, (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0));
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
        return list;
    }
}
