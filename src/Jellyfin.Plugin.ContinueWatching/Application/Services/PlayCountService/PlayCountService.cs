using System;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ContinueWatching.Infrastructure.Repositories;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using static Jellyfin.Plugin.ContinueWatching.Infrastructure.Repositories.PlayCountStore;

namespace Jellyfin.Plugin.ContinueWatching.Application.Services.PlayCountService;

/// <summary>
/// Makes <see cref="PlayCountStore"/> the source of truth for <c>UserItemData.PlayCount</c>.
/// <para>
/// Jellyfin raises its own <c>PlayCount</c> when playback <em>starts</em> -- verified on 12.1.0
/// against a pristine item: the <see cref="UserDataSaveReason.PlaybackStart"/> save already
/// carries <c>PlayCount == 1</c>. That save reaches <see cref="Reconcile"/> before
/// <see cref="RecordPlaybackStart"/> runs, so the two must agree on who owns the increment or
/// one viewing lands in the ledger twice.
/// </para>
/// <para>
/// Three rules, applied in <see cref="Reconcile"/>:
/// </para>
/// <list type="bullet">
/// <item>
/// An increase that lands inside <see cref="IncrementDedupeWindow"/> of a counted start is the
/// same viewing again -- a replay, or Jellyfin's own increment arriving a second time -- so the
/// ledger wins outright.
/// </item>
/// <item>
/// On a playback-start save the increment <em>is</em> this viewing. Adopt it and stamp it as
/// counted, which leaves <see cref="RecordPlaybackStart"/> to dedupe rather than add a second.
/// Adopting rather than rejecting is what keeps a count that predates the ledger intact.
/// </item>
/// <item>
/// Once a viewing is under way, a progress or finished save may not raise a tracked count: that
/// viewing was counted at its start. For every other save (a watched-state toggle, an import) the
/// ledger takes the higher of the two values, which is what makes the count survive a
/// mark-unwatched.
/// </item>
/// </list>
/// </summary>
public sealed class PlayCountService(
    PlayCountStore store,
    IUserDataManager userDataManager,
    ILogger<PlayCountService> logger) : IPlayCountService
{
    /// <summary>
    /// Jellyfin raises PlaybackStart more than once for a single viewing -- a transcode restart,
    /// a seek that opens a new session, a client reconnecting. Repeats inside this window are the
    /// same viewing, not a rewatch. Measured against the playback-reporting history: 56 of 754
    /// logged events sit within 15 minutes of an earlier start of the same item.
    /// </summary>
    private static readonly TimeSpan IncrementDedupeWindow = TimeSpan.FromMinutes(15);

    public void RecordPlaybackStart(User user, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);

        var key = new PlayCountKey(user.Id, item.Id);
        (int count, bool incremented) = store.Increment(key, DateTimeOffset.UtcNow, IncrementDedupeWindow);

        if (!incremented)
        {
            // Reconcile already adopted Jellyfin's own start increment for this viewing, or the
            // viewing is a repeat inside the dedupe window. Either way it is already counted.
            return;
        }

        logger.LogDebug("Play count for item {ItemId} raised to {Count} by playback start", item.Id, count);

        UserItemData? data = userDataManager.GetUserData(user, item);
        if (data is null)
        {
            return;
        }

        WriteCount(user, item, data, count);
    }

    public void Reconcile(User user, BaseItem item, UserItemData data, UserDataSaveReason reason)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(data);

        var key = new PlayCountKey(user.Id, item.Id);
        PlayCountDto? tracked = store.TryGet(key);

        var now = DateTimeOffset.UtcNow;

        // The same viewing counted again: a replay inside the window, or Jellyfin re-announcing
        // an increment it has already made. The save reason cannot be trusted to spot this --
        // Jellyfin 12 labels these saves inconsistently, the same unreliability already noted for
        // POST /PlayedItems in UserDataSaved -- so the test is whether this plugin counted a
        // start for the same item moments ago.
        bool duplicateOfCountedViewing =
            tracked is not null
            && tracked.LastCountedUtc is { } lastCounted
            && now - lastCounted < IncrementDedupeWindow
            && data.PlayCount > tracked.Count;

        // A viewing is counted once, at its start. Anything Jellyfin adds while that viewing is
        // still running -- most obviously its +1 when the item finishes -- is that same viewing
        // a second time. Only guarded when the item is tracked: with no ledger entry there is no
        // count to defend and rejecting would write a zero over a real one.
        bool midPlaybackSave =
            tracked is not null
            && reason is UserDataSaveReason.PlaybackProgress or UserDataSaveReason.PlaybackFinished;

        int ledgerCount;
        if (duplicateOfCountedViewing || midPlaybackSave)
        {
            ledgerCount = tracked!.Count;
        }
        else if (reason == UserDataSaveReason.PlaybackStart)
        {
            // Adopt Jellyfin's increment as this viewing's count and stamp it, so the
            // RecordPlaybackStart that follows dedupes instead of counting the viewing twice.
            ledgerCount = store.RaiseTo(key, data.PlayCount, now);
        }
        else
        {
            ledgerCount = store.RaiseTo(key, data.PlayCount, tracked?.LastCountedUtc);
        }

        if (data.PlayCount == ledgerCount)
        {
            return;
        }

        logger.LogDebug(
            "Play count for item {ItemId} corrected from {SavedCount} to {LedgerCount} on a {Reason} save",
            item.Id,
            data.PlayCount,
            ledgerCount,
            reason);

        WriteCount(user, item, data, ledgerCount);
    }

    private void WriteCount(User user, BaseItem item, UserItemData data, int count)
    {
        if (data.PlayCount == count)
        {
            return;
        }

        data.PlayCount = count;

        using (LedgerWrite.Begin())
        {
            userDataManager.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserData, CancellationToken.None);
        }
    }
}
