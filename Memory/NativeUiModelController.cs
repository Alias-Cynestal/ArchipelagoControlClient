using System.Diagnostics;
using System.Runtime.InteropServices;
using Ap.Control.Utils.Interfaces;

namespace Ap.Control.Memory
{
    /// <summary>
    /// Drives the Coherent UI model that the patched elevator panel reads, by raw memory write.
    /// </summary>
    public sealed class NativeUiModelController : IDisposable, IUiModelController
    {
        private const string ProcessName = "Control_DX12";

        private const long UiHudVtableRva = 0xE5E5E8;
        private const int OffOwner = 0x1A0;   // UIHud -> owning object
        private const int OffModel = 0x18;    // owning object -> the 5-bool model
        private const int FieldCount = 5;

        // User-mode heap pointer range on x64, used to sanity-check the chain.
        private const ulong PtrMin = 0x0000_0100_0000_0000;
        private const ulong PtrMax = 0x0000_7FFF_FFFF_FFFF;

        private IntPtr _hProc;
        private long _moduleBase;
        private long _modelAddr;   // 0 = unresolved

        public bool IsOpen => _hProc != IntPtr.Zero;

        /// <summary>Attach to the running game. Returns false (rather than throwing) if it isn't up.</summary>
        public bool EnsureStarted()
        {
            if (_hProc != IntPtr.Zero) return true;

            Process? proc = Process.GetProcessesByName(ProcessName).FirstOrDefault();
            if (proc?.MainModule is null) return false;

            IntPtr h = OpenProcess(PROCESS_ACCESS, false, proc.Id);
            if (h == IntPtr.Zero) return false;

            _hProc = h;
            _moduleBase = proc.MainModule.BaseAddress.ToInt64();
            _modelAddr = 0;
            return true;
        }

        public bool SetBits(IReadOnlySet<ElevatorBit> granted)
        {
            if (!EnsureStarted()) return false;

            var bytes = new byte[FieldCount];
            foreach (var bit in granted)
            {
                int i = (int)bit;
                if (i >= 0 && i < FieldCount) bytes[i] = 1;
            }

            long addr = ResolveModel();
            if (addr == 0) return false;

            if (WriteProcessMemory(_hProc, (IntPtr)addr, bytes, bytes.Length, out int put) && put == bytes.Length)
                return true;

            _modelAddr = 0;
            return false;
        }

        public byte[]? ReadRaw()
        {
            if (!EnsureStarted()) return null;
            long addr = ResolveModel();
            if (addr == 0) return null;

            var buf = new byte[FieldCount];
            if (!ReadProcessMemory(_hProc, (IntPtr)addr, buf, buf.Length, out int read) || read != buf.Length)
            {
                _modelAddr = 0;
                return null;
            }
            return buf;
        }

        /// <summary>
        /// Cached model address, re-scanning only when the cached one no longer reads as five boolean bytes.
        /// </summary>
        private long ResolveModel()
        {
            if (_modelAddr != 0)
            {
                var probe = new byte[FieldCount];
                if (ReadProcessMemory(_hProc, (IntPtr)_modelAddr, probe, probe.Length, out int n)
                    && n == probe.Length && probe.All(b => b <= 1))
                    return _modelAddr;
                _modelAddr = 0;
            }

            foreach (long hud in ScanForVtable(_moduleBase + UiHudVtableRva))
            {
                var ptr = new byte[8];
                if (!ReadProcessMemory(_hProc, (IntPtr)(hud + OffOwner), ptr, 8, out int r) || r != 8) continue;

                ulong owner = BitConverter.ToUInt64(ptr, 0);
                if (owner is < PtrMin or > PtrMax) continue;

                long model = (long)owner + OffModel;
                var probe = new byte[FieldCount];
                if (ReadProcessMemory(_hProc, (IntPtr)model, probe, probe.Length, out int n)
                    && n == probe.Length && probe.All(b => b <= 1))
                {
                    _modelAddr = model;
                    return model;
                }
            }
            return 0;
        }

        /// <summary>Every 8-byte-aligned occurrence of <paramref name="vtable"/> in committed private R/W memory.</summary>
        private IEnumerable<long> ScanForVtable(long vtable)
        {
            byte[] want = BitConverter.GetBytes(vtable);
            var buf = new byte[0x100000];
            IntPtr addr = IntPtr.Zero;

            while (VirtualQueryEx(_hProc, addr, out MEMORY_BASIC_INFORMATION mbi,
                       (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0)
            {
                long regionBase = mbi.BaseAddress.ToInt64();
                long regionSize = (long)mbi.RegionSize;

                bool candidate = mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE
                                 && (mbi.Protect & 0xFF) == PAGE_READWRITE;
                if (candidate)
                {
                    for (long off = 0; off < regionSize; off += buf.Length)
                    {
                        int len = (int)Math.Min(buf.Length, regionSize - off);
                        if (!ReadProcessMemory(_hProc, (IntPtr)(regionBase + off), buf, len, out int got) || got < 8)
                            continue;

                        for (int i = 0; i + 8 <= got; i += 8)
                        {
                            if (buf[i] == want[0] && buf[i + 1] == want[1]
                                && BitConverter.ToInt64(buf, i) == vtable)
                                yield return regionBase + off + i;
                        }
                    }
                }

                long next = regionBase + regionSize;
                if (next <= addr.ToInt64()) break;
                addr = (IntPtr)next;
            }
        }

        public void Dispose()
        {
            if (_hProc != IntPtr.Zero) { CloseHandle(_hProc); _hProc = IntPtr.Zero; }
        }

        // --- P/Invoke -----------------------------------------------------------------------------
        private const int PROCESS_ACCESS =
            0x0400 /*QUERY_INFORMATION*/ | 0x0010 /*VM_READ*/ | 0x0020 /*VM_WRITE*/ | 0x0008 /*VM_OPERATION*/;
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
}
