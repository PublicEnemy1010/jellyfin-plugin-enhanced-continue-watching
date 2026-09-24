using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ContinueWatching.Application.Services.PlayCountService;

public interface IPlayCountService
{
    /// <summary>
    /// Counts a viewing. Called when playback starts, which is the only thing that raises a
    /// play count -- marking an item watched never does.
    /// </summary>
    void RecordPlaybackStart(User user, BaseItem item);

    /// <summary>
    /// Re-applies the ledger's count to an item whose user data Jellyfin has just written,
    /// undoing the reset that a mark-unwatched performs.
    /// </summary>
    void Reconcile(User user, BaseItem item, UserItemData data, UserDataSaveReason reason);
}
