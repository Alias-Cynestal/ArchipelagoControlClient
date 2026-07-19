using System.Diagnostics;
using System.Runtime.InteropServices;
using Ap.Control.Memory;

namespace Ap.Control.Utils.GameHook
{
    /// <summary>
    /// Read-only access to another process's virtual memory: opens the process, walks its committed
    /// private read/write regions, scans for byte signatures, and reads bytes back. Windows x64 only;
    /// the host app must be x64 to match the game.
    ///
    /// Shared low-level layer. <see cref="NativeItemGranter"/> keeps its own write/execute P/Invokes
    /// (VirtualAllocEx / WriteProcessMemory / CreateRemoteThread) since those are specific to its
    /// shellcode path; this accessor is the read/scan half used by the memory-backed save source.
    /// </summary>
    public sealed class ProcessMemoryAccessor : IDisposable
    {
        private const int PROCESS_QUERY_INFORMATION = 0x0400;
        private const int PROCESS_VM_READ = 0x0010;

        private IntPtr _hProc;

        public int ProcessId { get; }
        public string ProcessName { get; }
        public bool IsOpen => _hProc != IntPtr.Zero;

        private ProcessMemoryAccessor(IntPtr hProc, int pid, string name)
        {
            _hProc = hProc;
            ProcessId = pid;
            ProcessName = name;
        }

        /// <summary>True if at least one process with this name is currently running.</summary>
        public static bool IsProcessRunning(string processName)
            => MemoryHelper.IsProcessRunning(processName);

        /// <summary>
        /// Open the first process with the given name for reading. Returns null if it isn't running.
        /// Throws <see cref="InvalidOperationException"/> if the process exists but can't be opened
        /// (typically an elevation problem).
        /// </summary>
        public static ProcessMemoryAccessor? TryOpen(string processName)
        {
            Process? proc = MemoryHelper.TryGetProcess(processName);
            if (proc is null) return null;

            IntPtr h = MemoryHelper.OpenProcessHandle(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, proc.Id);
            if (h == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"OpenProcess({processName}) failed (error {Marshal.GetLastWin32Error()}). Try running as administrator.");
            return new ProcessMemoryAccessor(h, proc.Id, proc.ProcessName);
        }

        /// <summary>
        /// Scan every committed, private, read/write region for <paramref name="pattern"/>, returning the
        /// absolute address of each match for which <paramref name="validate"/> returns true.
        /// <paramref name="lookahead"/> is how many bytes past the pattern the validator may inspect;
        /// windows overlap by <c>pattern.Length + lookahead</c> so matches never fall through the cracks.
        /// </summary>
        public List<long> ScanSignature(byte[] pattern, int lookahead, Func<byte[], int, bool>? validate = null,
            CancellationToken ct = default)
        {
            if (pattern.Length == 0) throw new ArgumentException("pattern must not be empty", nameof(pattern));
            var hits = new List<long>();
            int need = pattern.Length + lookahead;

            var buf = new byte[0x100000];              // 1 MiB scan window
            int step = buf.Length - need;              // overlap so edge-straddling matches are caught
            if (step <= 0) throw new ArgumentException("lookahead too large for the scan window", nameof(lookahead));

            IntPtr addr = IntPtr.Zero;
            foreach (var region in MemoryHelper.EnumerateCommittedPrivateReadWriteRegions(_hProc))
            {
                ct.ThrowIfCancellationRequested();

                long regionBase = region.Base;
                long regionSize = region.Size;

                for (long off = 0; off < regionSize; off += step)
                {
                    int toRead = (int)Math.Min(buf.Length, regionSize - off);
                    if (!MemoryHelper.TryReadBytes(_hProc, (IntPtr)(regionBase + off), buf, toRead, out int read) || read < need)
                        continue;

                    int limit = read - need;
                    for (int i = 0; i <= limit; i++)
                    {
                        if (buf[i] != pattern[0]) continue;
                        bool m = true;
                        for (int k = 1; k < pattern.Length; k++)
                            if (buf[i + k] != pattern[k]) { m = false; break; }
                        if (!m) continue;

                        if (validate is null || validate(buf, i))
                            hits.Add(regionBase + off + i);
                    }
                }
            }
            return hits;
        }

        /// <summary>
        /// Read up to <paramref name="maxLen"/> bytes at <paramref name="addr"/>, clamped to the end of the
        /// containing region so a large read never fails against a page gap. Returns however many bytes were
        /// actually read (possibly empty).
        /// </summary>
        public byte[] ReadClamped(long addr, int maxLen)
        {
            long avail = maxLen;
            foreach (var region in MemoryHelper.EnumerateRegions(_hProc))
            {
                if (region.Base <= addr && addr < region.Base + region.Size)
                {
                    long regionEnd = region.Base + region.Size;
                    avail = Math.Min(maxLen, regionEnd - addr);
                    break;
                }
            }
            if (avail <= 0) return Array.Empty<byte>();

            var buf = new byte[avail];
            if (!MemoryHelper.TryReadBytes(_hProc, (IntPtr)addr, buf, buf.Length, out int read) || read <= 0)
                return Array.Empty<byte>();
            if (read != buf.Length) Array.Resize(ref buf, read);
            return buf;
        }

        /// <summary>Read a little-endian uint32; false if the memory couldn't be read.</summary>
        public bool TryReadU32(long addr, out uint value)
        {
            byte[]? bytes = MemoryHelper.TryReadExact(_hProc, (IntPtr)addr, 4);
            if (bytes is not null)
            {
                value = BitConverter.ToUInt32(bytes, 0);
                return true;
            }
            value = 0;
            return false;
        }

        /// <summary>Read exactly <paramref name="len"/> bytes; null if the full amount couldn't be read.</summary>
        public byte[]? TryReadExact(long addr, int len)
        {
            return MemoryHelper.TryReadExact(_hProc, (IntPtr)addr, len);
        }

        public void Dispose()
        {
            MemoryHelper.CloseHandleSafe(_hProc); _hProc = IntPtr.Zero;
        }
    }
}
