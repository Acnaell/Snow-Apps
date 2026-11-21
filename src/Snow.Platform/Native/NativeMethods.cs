using System.Runtime.InteropServices;

namespace Snow.Platform.Native;

internal static partial class NativeMethods
{
    // ───────── user32.dll: janela em primeiro plano (só o id do processo dono; nada é aberto) ─────────
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // ───────── powrprof.dll ─────────
    [DllImport("powrprof.dll")] internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")] internal static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);
    [DllImport("powrprof.dll")] internal static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupGuid, uint accessFlags, uint index, byte[]? buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] internal static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid, IntPtr settingGuid, byte[]? buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] internal static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);
    [DllImport("powrprof.dll")] internal static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);
    // Exportadas desde o Windows 10 1709 (controle "Modo de energia").
    [DllImport("powrprof.dll")] internal static extern uint PowerGetActualOverlayScheme(out Guid actualOverlayGuid);
    [DllImport("powrprof.dll")] internal static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);
    [DllImport("powrprof.dll")] internal static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferLength, byte[] outputBuffer, uint outputBufferLength);

    internal const uint ACCESS_SCHEME = 16;
    internal const int SystemPowerCapabilities = 4;
    internal const uint ERROR_NO_MORE_ITEMS = 259;

    // ───────── kernel32.dll ─────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);
    internal const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr hMem);
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();

    // ───────── advapi32 / ntdll (lista Standby) ─────────
    [StructLayout(LayoutKind.Sequential)] internal struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] internal struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")] internal static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    internal const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;
    internal const int SystemMemoryListInformation = 80, MemoryPurgeStandbyList = 4;

    // ───────── shell32 ─────────
    [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsUserAnAdmin();
}
