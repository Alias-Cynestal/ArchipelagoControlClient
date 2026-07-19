using Ap.Control.Models;
using Ap.Control.SaveFile;
using Ap.Control.Utils.GameHook;
using Ap.Control.Utils.Interfaces;
using Ap.Control.Utils.Save;

namespace Ap.Control.Memory
{
    /// <summary>
    /// Reads Control's save directly from the running game's process memory.
    /// </summary>
    public sealed class SaveMemoryWatcher : ISaveWatcher
    {
        // Save-blob header signature: magic(8) + crc32(4) + filenameLen(4)=10 + "persistent".
        private static readonly byte[] Magic = { 6, 0, 0, 0, 6, 0, 0, 0 };
        private static readonly byte[] FilenameSig =
        {
            0x0A, 0, 0, 0,
            0x70, 0x65, 0x72, 0x73, 0x69, 0x73, 0x74, 0x65, 0x6E, 0x74, // "persistent"
        };
        private const int OffCrc = 8;                 // CRC32 field, relative to the magic
        private const int HeaderProbeLen = 12 + 14;   // magic(8)+crc(4) then the 14-byte filename sig
        private const int Lookahead = HeaderProbeLen - 8;  // bytes past the magic the validator inspects
        private const int MaxBlobBytes = 8 * 1024 * 1024;  // generous window; parser stops after NumChunks

        private readonly string _processName;
        private readonly ISaveFileParser _parser;
        private readonly int _pollMs;
        private readonly TimeSpan _rescanInterval;

        private readonly List<ISaveChangeNotifier> _notifiers = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _cts = new();

        // Candidate blob addresses and the CRC each held last time we looked.
        private readonly Dictionary<long, uint> _crcByAddr = new();

        private ProcessMemoryAccessor? _accessor;
        private DateTime _lastScanUtc = DateTime.MinValue;
        private Timer? _timer;
        private ControlSave? _current;
        private uint _currentCrc;
        private bool _haveCurrent;
        private bool _emitInitial;
        private bool _started;
        private bool _disposed;

        public event EventHandler<SaveChangedEventArgs>? SaveChanged;
        public event EventHandler<SaveWatchErrorEventArgs>? Error;

        public ControlSave? Current => _current;

        public SaveMemoryWatcher(
            ISaveFileParser? parser = null,
            string processName = "Control_DX12",
            int pollMs = 1000,
            int rescanMs = 30000)
        {
            _parser = parser ?? new ControlSaveParser();
            _processName = processName;
            _pollMs = pollMs;
            _rescanInterval = TimeSpan.FromMilliseconds(rescanMs);
        }

        public ISaveWatcher AddNotifier(ISaveChangeNotifier notifier)
        {
            ArgumentNullException.ThrowIfNull(notifier);
            _notifiers.Add(notifier);
            return this;
        }

        public Task StartAsync(bool emitInitial = false, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return Task.CompletedTask;
            _started = true;

            _emitInitial = emitInitial;
            // Kick the first tick immediately, then poll on an interval.
            _timer = new Timer(_ => _ = TickAsync(), null, 0, _pollMs);
            return Task.CompletedTask;
        }

        private async Task TickAsync()
        {
            if (_cts.IsCancellationRequested) return;
            if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                if (!EnsureOpenAndScanned()) return;

                long? changedAnchor = null;
                uint changedCrc = 0;

                foreach (long addr in _crcByAddr.Keys.ToList())
                {
                    if (!_accessor!.TryReadU32(addr + OffCrc, out uint crc))
                    {
                        _crcByAddr.Remove(addr);
                        _lastScanUtc = DateTime.MinValue;
                        continue;
                    }

                    uint prev = _crcByAddr[addr];
                    _crcByAddr[addr] = crc;

                    if (crc != prev && crc != _currentCrc && ValidateSignature(addr))
                    {
                        changedAnchor = addr;
                        changedCrc = crc;
                    }
                }

                if (!_haveCurrent)
                {
                    await AdoptBaselineAsync().ConfigureAwait(false);
                    return;
                }

                if (changedAnchor is long anchor)
                    await ParseAndDispatchAsync(anchor, changedCrc).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Make sure the accessor is open and the candidate set is populated.
        /// </summary>
        private bool EnsureOpenAndScanned()
        {
            if (_accessor is not null && !ProcessMemoryAccessor.IsProcessRunning(_processName))
            {
                _accessor.Dispose();
                _accessor = null;
                _crcByAddr.Clear();
                _haveCurrent = false;
                _current = null;
                _currentCrc = 0;
            }

            if (_accessor is null)
            {
                _accessor = ProcessMemoryAccessor.TryOpen(_processName);
                if (_accessor is null) return false;
                _lastScanUtc = DateTime.MinValue;
            }

            bool due = DateTime.UtcNow - _lastScanUtc >= _rescanInterval;
            if (_crcByAddr.Count == 0 || due)
                Rescan();

            return _crcByAddr.Count > 0;
        }

        /// <summary>Scan for all save-blob copies</summary>
        private void Rescan()
        {
            _lastScanUtc = DateTime.UtcNow;
            List<long> hits = _accessor!.ScanSignature(
                Magic, Lookahead,
                static (buf, i) =>
                {
                    for (int k = 0; k < FilenameSig.Length; k++)
                        if (buf[i + 12 + k] != FilenameSig[k]) return false;
                    return true;
                },
                _cts.Token);

            var seen = new HashSet<long>(hits);
            foreach (long gone in _crcByAddr.Keys.Where(a => !seen.Contains(a)).ToList())
                _crcByAddr.Remove(gone);
            foreach (long addr in hits)
            {
                if (_crcByAddr.ContainsKey(addr)) continue;
                _crcByAddr[addr] = _accessor.TryReadU32(addr + OffCrc, out uint crc) ? crc : 0;
            }
        }

        /// <summary>Confirm the address still begins with a valid save-blob header.</summary>
        private bool ValidateSignature(long addr)
        {
            byte[]? head = _accessor!.TryReadExact(addr, HeaderProbeLen);
            if (head is null) return false;
            for (int k = 0; k < Magic.Length; k++)
                if (head[k] != Magic[k]) return false;
            for (int k = 0; k < FilenameSig.Length; k++)
                if (head[12 + k] != FilenameSig[k]) return false;
            return true;
        }

        /// <summary>Parse whichever copy currently parses, set it as the baseline, and (if requested) dispatch it as the initial state.</summary>
        private async Task AdoptBaselineAsync()
        {
            foreach (long addr in _crcByAddr.Keys)
            {
                ControlSave? save = TryParseAt(addr);
                if (save is null) continue;

                _current = save;
                _accessor!.TryReadU32(addr + OffCrc, out _currentCrc);
                _haveCurrent = true;

                if (_emitInitial)
                {
                    var diff = SaveDiffer.Diff(null, save);
                    if (diff.HasChanges)
                        await DispatchAllAsync(save, previous: null, diff, addr).ConfigureAwait(false);
                }
                return;
            }
        }

        private async Task ParseAndDispatchAsync(long addr, uint crc)
        {
            ControlSave? save = TryParseAt(addr);
            if (save is null) return;

            ControlSave? previous = _current;
            var diff = SaveDiffer.Diff(previous, save);

            _current = save;
            _currentCrc = crc;

            if (diff.HasChanges)
                await DispatchAllAsync(save, previous, diff, addr).ConfigureAwait(false);
        }

        private async Task DispatchAllAsync(ControlSave save, ControlSave? previous, SaveDiff diff, long addr)
        {
            var args = BuildArgs(save, previous, diff, addr);
            SaveChanged?.Invoke(this, args);

            foreach (var notifier in _notifiers)
            {
                try
                {
                    await notifier.NotifyAsync(args, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    OnError(ex);
                }
            }
        }

        private ControlSave? TryParseAt(long addr)
        {
            try
            {
                byte[] bytes = _accessor!.ReadClamped(addr, MaxBlobBytes);
                if (bytes.Length < HeaderProbeLen) return null;
                return _parser.Parse(bytes);
            }
            catch
            {
                return null;
            }
        }

        private SaveChangedEventArgs BuildArgs(ControlSave save, ControlSave? previous, SaveDiff diff, long addr)
            => new()
            {
                Save = save,
                Previous = previous,
                Diff = diff,
                Path = $"memory:{_processName}@0x{addr:X}",
            };

        private void OnError(Exception ex)
            => Error?.Invoke(this, new SaveWatchErrorEventArgs { Exception = ex, Path = $"memory:{_processName}" });

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _cts.Cancel(); } catch { /* ignore */ }
            _timer?.Dispose();
            _accessor?.Dispose();
            _gate.Dispose();
            _cts.Dispose();
        }
    }
}
