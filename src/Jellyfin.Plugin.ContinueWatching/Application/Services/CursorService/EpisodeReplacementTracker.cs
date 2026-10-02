using System;
using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ContinueWatching.Application.Services.CursorService;

/// <summary>
/// Remembers recently removed episodes by show, season and episode number, so the item that
/// replaces one (a quality upgrade, a disk to stream move, a re-grab) is recognised as the same
/// episode rather than a new one. In memory only: a restart in between loses the link, and the
/// read-time repair in <c>ContinueWatchingSection</c> covers that.
/// </summary>
public sealed class EpisodeReplacementTracker
{
    // An upgrade re-imports within seconds; a stream re-grab can take days.
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private readonly ConcurrentDictionary<(string Show, int Season, int Episode), (Guid ItemId, DateTime RemovedUtc)> removed = new();

    private DateTime nextPruneUtc = DateTime.MinValue;

    public void RecordRemoved(Episode episode, string? showKey)
    {
        if (Key(episode, showKey) is not { } key)
        {
            return;
        }

        Prune();
        removed[key] = (episode.Id, DateTime.UtcNow);
    }

    /// <summary>
    /// Returns the id of the removed item that <paramref name="episode"/> replaces, and forgets it.
    /// </summary>
    public Guid? TakeReplaced(Episode episode, string? showKey)
    {
        if (Key(episode, showKey) is not { } key || !removed.TryRemove(key, out var entry))
        {
            return null;
        }

        return DateTime.UtcNow - entry.RemovedUtc <= Lifetime ? entry.ItemId : null;
    }

    /// <summary>
    /// Returns whether <paramref name="itemId"/> was removed recently enough that its
    /// replacement may still arrive (e.g. a stream re-grab in progress).
    /// </summary>
    public bool IsAwaitingReplacement(Guid itemId)
    {
        DateTime now = DateTime.UtcNow;
        foreach (var (_, entry) in removed)
        {
            if (entry.ItemId == itemId && now - entry.RemovedUtc <= Lifetime)
            {
                return true;
            }
        }

        return false;
    }

    private static (string, int, int)? Key(Episode episode, string? showKey) =>
        episode.ParentIndexNumber is int season
        && episode.IndexNumber is int number
        && !string.IsNullOrEmpty(showKey)
            ? (showKey, season, number)
            : null;

    private void Prune()
    {
        DateTime now = DateTime.UtcNow;
        if (now < nextPruneUtc)
        {
            return;
        }

        nextPruneUtc = now.AddHours(1);
        foreach (var (key, entry) in removed)
        {
            if (now - entry.RemovedUtc > Lifetime)
            {
                removed.TryRemove(key, out _);
            }
        }
    }
}
