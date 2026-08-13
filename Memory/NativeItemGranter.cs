using System.Diagnostics;
using System.Runtime.InteropServices;
using Ap.Control.Models;
using Ap.Control.Utils.Interfaces;

namespace Ap.Control.Memory
{
    public sealed class NativeItemGranter : IItemGranter
    {
        // Build-specific addresses come from GameBuildProfile, keyed on the executable's hash.
        private const int  OFF_IS_PLAYER       = 0x90;       // byte flag == 1 on the player's inventory
        private const string ProcessName       = "Control_DX12";
        private const string CoregameModule     = "coregame_rmdwin10_f.dll";

        private IntPtr _hProc;
        private IntPtr _imageBase;
        private long   _coregameBase;
        private IntPtr _playerSelf;
        private GameBuildProfile? _profile;
        private string? _buildError;   // set when the running build has no profile; reported instead of a generic failure
        private readonly List<IntPtr> _candidates = new();

        public bool IsReady => _playerSelf != IntPtr.Zero;

        /// <summary>Number of player-flagged inventory objects found by the last scan.</summary>
        public int CandidateCount => _candidates.Count;


        private const int OFF_NET_ROLE_FIELD = 0x18; // Top 2 bits = network role (2 or 3)
        private const int OFF_ITEM_COUNT = 0x48; // Regular item count (the vector at +0x40)

        private int RoleOf(IntPtr c) { try { return (int)((MemoryHelper.ReadU64(_hProc, c + OFF_NET_ROLE_FIELD) >> 62) & 3); } catch { return -1; } }
        private uint ItemsOf(IntPtr c) { try { return MemoryHelper.ReadU32(_hProc, c + OFF_ITEM_COUNT); } catch { return 0; } }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCore();
            return Task.CompletedTask;
        }

        private void StartCore()
        {
            if (_hProc != IntPtr.Zero) return;

            Process proc = MemoryHelper.GetProcessOrThrow(ProcessName);

            _profile = GameBuildRegistry.Resolve(proc);   // throws UnsupportedGameBuildException

            _imageBase = proc.MainModule?.BaseAddress
                ?? throw new InvalidOperationException("Could not read the game's image base.");

            foreach (ProcessModule m in proc.Modules)
                if (string.Equals(m.ModuleName, CoregameModule, StringComparison.OrdinalIgnoreCase))
                { _coregameBase = m.BaseAddress.ToInt64(); break; }

            _hProc = MemoryHelper.OpenProcessHandle(PROCESS_ACCESS, false, proc.Id);
            if (_hProc == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"OpenProcess failed (error {Marshal.GetLastWin32Error()}). Try running as administrator.");

            _playerSelf = FindPlayerInventory();
        }

        private bool EnsureStarted()
        {
            if (_hProc != IntPtr.Zero) return true;
            try { StartCore(); _buildError = null; }
            catch (UnsupportedGameBuildException e) { _buildError = e.Message; }
            catch { /* game not running / not openable yet */ }
            return _hProc != IntPtr.Zero;
        }

        /// <summary>Why the granter could not attach — the build error if there is one, else the generic cause.</summary>
        private string NotStartedReason =>
            _buildError ?? $"granter not started — is {ProcessName} running?";

        public Task<GrantResult> GiveItemAsync(ulong gid, float parameter = 1.0f, CancellationToken cancellationToken = default)
            => Task.Run(() => GiveItem(gid, parameter), cancellationToken);

        // 'parameter' is the item's engine Parameter float (stored on the spawned item at +0x58);
        private GrantResult GiveItem(ulong gid, float parameter)
        {
            if (!EnsureStarted()) return GrantResult.Fail(NotStartedReason);

            if (!IsValidPlayerInventory(_playerSelf))
            {
                _playerSelf = FindPlayerInventory();
                if (_playerSelf == IntPtr.Zero)
                    return GrantResult.Fail("player inventory not found — is a save loaded?");
            }

            IntPtr def = IntPtr.Zero, code = IntPtr.Zero, result = IntPtr.Zero;
            try
            {
                def = VirtualAllocEx(_hProc, IntPtr.Zero, 24, MEM_COMMIT_RESERVE, PAGE_READWRITE);
                if (def == IntPtr.Zero) return GrantResult.Fail("VirtualAllocEx(def) failed");
                MemoryHelper.WriteBytes(_hProc, def, BitConverter.GetBytes(gid));

                result = VirtualAllocEx(_hProc, IntPtr.Zero, 8, MEM_COMMIT_RESERVE, PAGE_READWRITE);
                if (result == IntPtr.Zero) return GrantResult.Fail("VirtualAllocEx(result) failed");

                byte[] shellcode = BuildShellcode(
                    thisPtr: _playerSelf,
                    defPtr: def,
                    parameter: parameter,
                    giveFn: (IntPtr)(_imageBase.ToInt64() + Profile.GiveItemFromDefinition),
                    resultSlot: result);

                code = VirtualAllocEx(_hProc, IntPtr.Zero, shellcode.Length, MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
                if (code == IntPtr.Zero) return GrantResult.Fail("VirtualAllocEx(code) failed");
                MemoryHelper.WriteBytes(_hProc, code, shellcode);

                IntPtr thread = CreateRemoteThread(_hProc, IntPtr.Zero, 0, code, IntPtr.Zero, 0, out _);
                if (thread == IntPtr.Zero) return GrantResult.Fail("CreateRemoteThread failed");

                uint wait = WaitForSingleObject(thread, 5000);
                MemoryHelper.CloseHandleSafe(thread);
                if (wait != 0) return GrantResult.Fail("remote give-item thread did not finish in time");

                ulong ret = MemoryHelper.ReadU64(_hProc, result);   // FUN_1403b6c30 returns the spawned object, 0 on failure
                return new GrantResult { Ok = true, Accepted = ret != 0 };
            }
            catch (Exception e)
            {
                return GrantResult.Fail(e.Message);
            }
            finally
            {
                if (def != IntPtr.Zero) VirtualFreeEx(_hProc, def, 0, MEM_RELEASE);
                if (code != IntPtr.Zero) VirtualFreeEx(_hProc, code, 0, MEM_RELEASE);
                if (result != IntPtr.Zero) VirtualFreeEx(_hProc, result, 0, MEM_RELEASE);
            }
        }

        private static byte[] BuildShellcode(IntPtr thisPtr, IntPtr defPtr, float parameter, IntPtr giveFn, IntPtr resultSlot)
        {
            uint amtBits = BitConverter.ToUInt32(BitConverter.GetBytes(parameter));
            var b = new List<byte>();
            b.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });                 // sub rsp, 0x28
            b.AddRange(new byte[] { 0x48, 0xB9 }); b.AddRange(BitConverter.GetBytes(thisPtr.ToInt64()));   // mov rcx, this
            b.AddRange(new byte[] { 0xBA, 0x01, 0x00, 0x00, 0x00 });           // mov edx, 1
            b.AddRange(new byte[] { 0x49, 0xB8 }); b.AddRange(BitConverter.GetBytes(defPtr.ToInt64()));    // mov r8, def
            b.AddRange(new byte[] { 0xB8 }); b.AddRange(BitConverter.GetBytes(amtBits));                   // mov eax, amountBits
            b.AddRange(new byte[] { 0x66, 0x0F, 0x6E, 0xD8 });                 // movd xmm3, eax
            b.AddRange(new byte[] { 0x48, 0xB8 }); b.AddRange(BitConverter.GetBytes(giveFn.ToInt64()));    // mov rax, giveFn
            b.AddRange(new byte[] { 0xFF, 0xD0 });                             // call rax
            b.AddRange(new byte[] { 0x49, 0xBA }); b.AddRange(BitConverter.GetBytes(resultSlot.ToInt64()));// mov r10, resultSlot
            b.AddRange(new byte[] { 0x49, 0x89, 0x02 });                       // mov [r10], rax
            b.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });                 // add rsp, 0x28
            b.Add(0xC3);                                                       // ret
            return b.ToArray();
        }

        // --- Player-inventory discovery -------------------------------------------------------

        /// <summary>The resolved build profile. Non-null once <see cref="EnsureStarted"/> has returned true.</summary>
        private GameBuildProfile Profile =>
            _profile ?? throw new InvalidOperationException("game build has not been identified yet.");

        private bool IsValidPlayerInventory(IntPtr candidate)
        {
            if (candidate == IntPtr.Zero) return false;
            try
            {
                ulong vtbl = MemoryHelper.ReadU64(_hProc, candidate);
                if (vtbl != (ulong)(_imageBase.ToInt64() + Profile.InventoryVtable)) return false;
                return MemoryHelper.ReadByte(_hProc, candidate + OFF_IS_PLAYER) == 1;
            }
            catch { return false; }
        }

        /// <summary>
        /// Scan committed private R/W memory for the inventory vtable, filtering on the player flag,
        /// collecting ALL matches into <see cref="_candidates"/>. Returns the first, or Zero.
        /// </summary>
        private IntPtr FindPlayerInventory()
        {
            _candidates.Clear();
            ulong wantVtbl = (ulong)(_imageBase.ToInt64() + Profile.InventoryVtable);
            byte[] want = BitConverter.GetBytes(wantVtbl);

            var buf = new byte[0x100000];   // 1 MiB scan window

            foreach (var mbi in MemoryHelper.EnumerateCommittedPrivateReadWriteRegions(_hProc))
            {
                long regionBase = mbi.Base;
                long regionSize = mbi.Size;

                for (long off = 0; off < regionSize; off += buf.Length)
                {
                    int toRead = (int)Math.Min(buf.Length, regionSize - off);
                    if (!MemoryHelper.TryReadBytes(_hProc, (IntPtr)(regionBase + off), buf, toRead, out int read) || read < 8)
                        continue;

                    for (int i = 0; i + 8 <= read; i += 8)   // objects are 8-byte aligned; vtable ptr at object+0
                    {
                        if (buf[i] == want[0] && buf[i + 1] == want[1]
                            && BitConverter.ToUInt64(buf, i) == wantVtbl)
                        {
                            IntPtr obj = (IntPtr)(regionBase + off + i);
                            try { if (MemoryHelper.ReadByte(_hProc, obj + OFF_IS_PLAYER) == 1) _candidates.Add(obj); }
                            catch { /* keep scanning */ }
                        }
                    }
                }
            }

            // Two networked replicas exist with identical items; only the authoritative one (network role 3) reflects grants in-game
            foreach (IntPtr c in _candidates)
                if (RoleOf(c) == 3) return c;

            IntPtr best = IntPtr.Zero;
            uint bestItems = 0;
            foreach (IntPtr c in _candidates)
            {
                uint n = ItemsOf(c);
                if (best == IntPtr.Zero || n > bestItems) { best = c; bestItems = n; }
            }
            return best;
        }

        // --- P/Invoke -------------------------------------------------------------------------

        private const int PROCESS_ACCESS = 0x0008 /*VM_OPERATION*/ | 0x0010 /*VM_READ*/ | 0x0020 /*VM_WRITE*/
                                          | 0x0002 /*CREATE_THREAD*/ | 0x0400 /*QUERY_INFORMATION*/;
        private const uint MEM_COMMIT_RESERVE = 0x1000 | 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, int size, uint type, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, int size, uint type);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attrs, uint stackSize, IntPtr start,
            IntPtr param, uint flags, out uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr h, uint ms);

        public ValueTask DisposeAsync()
        {
            MemoryHelper.CloseHandleSafe(_hProc);
            _hProc = IntPtr.Zero;
            return ValueTask.CompletedTask;
        }
    }
}
