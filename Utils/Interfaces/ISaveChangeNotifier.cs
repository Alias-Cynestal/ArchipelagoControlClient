using Ap.Control.Models;

namespace Ap.Control.Utils.Interfaces
{
    public interface ISaveChangeNotifier
    {
        Task NotifyAsync(SaveChangedEventArgs change, CancellationToken cancellationToken = default);
    }
}
