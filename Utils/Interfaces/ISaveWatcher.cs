using Ap.Control.Models;
using Ap.Control.SaveFile;

namespace Ap.Control.Utils.Interfaces
{
    /// <summary>
    /// Watches Control's save state from some source (a file on disk, or the running game's process
    /// memory), parsing it into a <see cref="ControlSave"/> and raising a diff whenever it changes.
    /// </summary>
    public interface ISaveWatcher : IDisposable
    {
        /// <summary>The most recently parsed save, or null if nothing has been read yet.</summary>
        ControlSave? Current { get; }

        event EventHandler<SaveChangedEventArgs>? SaveChanged;
        event EventHandler<SaveWatchErrorEventArgs>? Error;

        /// <summary>Register a notifier to receive every change. Returns this for chaining.</summary>
        ISaveWatcher AddNotifier(ISaveChangeNotifier notifier);

        /// <summary>
        /// Begin watching. When <paramref name="emitInitial"/> is true, the current save state is
        /// dispatched once on startup (diffed against nothing) so consumers can sync existing progress.
        /// </summary>
        Task StartAsync(bool emitInitial = false, CancellationToken cancellationToken = default);
    }
}
