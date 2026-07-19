using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ap.Control.Memory;

internal static class MemoryHelper
{
    internal readonly record struct MemoryRegion(IntPtr BaseAddress, UIntPtr RegionSize, uint State, uint Protect, uint Type)
    {
        public long Base => BaseAddress.ToInt64();
        public long Size => (long)RegionSize;
    }

    public static bool IsProcessRunning(string processName)
        => Process.GetProcessesByName(processName).Length > 0;

    public static Process GetProcessOrThrow(string processName)
        => Process.GetProcessesByName(processName).FirstOrDefault()
           ?? throw new InvalidOperationException($"{processName}.exe is not running.");

    public static Process? TryGetProcess(string processName)
        => Process.GetProcessesByName(processName).FirstOrDefault();

    public static IntPtr OpenProcessByName(string processName, int access)
    {
        using Process proc = GetProcessOrThrow(processName);
        return OpenProcess(access, false, proc.Id);
    }

    public static IntPtr OpenProcessHandle(int access, bool inherit, int pid)
        => OpenProcess(access, inherit, pid);

    public static void CloseHandleSafe(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            CloseHandle(handle);
    }

    public static IEnumerable<MemoryRegion> EnumerateRegions(IntPtr hProc)
    {
        IntPtr addr = IntPtr.Zero;
        while (VirtualQueryEx(hProc, addr, out MEMORY_BASIC_INFORMATION mbi,
                   (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0)
        {
            yield return new MemoryRegion(mbi.BaseAddress, mbi.RegionSize, mbi.State, mbi.Protect, mbi.Type);

            long next = mbi.BaseAddress.ToInt64() + (long)mbi.RegionSize;
            if (next <= mbi.BaseAddress.ToInt64())
                yield break;

            addr = (IntPtr)next;
        }
    }

    public static IEnumerable<MemoryRegion> EnumerateCommittedPrivateReadWriteRegions(IntPtr hProc)
        => EnumerateRegions(hProc).Where(r => r.State == MEM_COMMIT && r.Type == MEM_PRIVATE && r.Protect == PAGE_READWRITE);

    public static bool TryReadBytes(IntPtr hProc, IntPtr addr, byte[] buffer, int size, out int read)
        => ReadProcessMemory(hProc, addr, buffer, size, out read) && read == size;

    public static byte[] ReadBytes(IntPtr hProc, IntPtr addr, int size)
    {
        var buffer = new byte[size];
        if (!TryReadBytes(hProc, addr, buffer, size, out _))
            throw new InvalidOperationException($"ReadProcessMemory failed (error {Marshal.GetLastWin32Error()})");
        return buffer;
    }

    public static byte[]? TryReadExact(IntPtr hProc, IntPtr addr, int size)
    {
        var buffer = new byte[size];
        if (ReadProcessMemory(hProc, addr, buffer, size, out int read) && read == size)
            return buffer;
        return null;
    }

    public static byte ReadByte(IntPtr hProc, IntPtr addr) => ReadBytes(hProc, addr, 1)[0];
    public static uint ReadU32(IntPtr hProc, IntPtr addr) => BitConverter.ToUInt32(ReadBytes(hProc, addr, 4), 0);
    public static int ReadI32(IntPtr hProc, IntPtr addr) => BitConverter.ToInt32(ReadBytes(hProc, addr, 4), 0);
    public static ulong ReadU64(IntPtr hProc, IntPtr addr) => BitConverter.ToUInt64(ReadBytes(hProc, addr, 8), 0);

    public static void WriteBytes(IntPtr hProc, IntPtr addr, byte[] data)
    {
        if (!WriteProcessMemory(hProc, addr, data, data.Length, out int written) || written != data.Length)
            throw new InvalidOperationException($"WriteProcessMemory failed (error {Marshal.GetLastWin32Error()})");
    }

    public static void WriteI32(IntPtr hProc, IntPtr addr, int value)
        => WriteBytes(hProc, addr, BitConverter.GetBytes(value));

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_PRIVATE = 0x20000;
    private const uint PAGE_READWRITE = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buffer, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buffer, int size, out int written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, uint length);
}
