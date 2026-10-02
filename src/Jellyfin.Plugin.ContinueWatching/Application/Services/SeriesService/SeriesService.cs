using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ContinueWatching.Application.Services.SeriesService;

public sealed class SeriesService(ILibraryManager libraryManager, IUserDataManager userDataManager) : ISeriesService
{
    public Task<Guid?> GetNextEpisodeId(User user, Guid seriesId, Guid episodeId)
    {
        List<Episode>? episodes = GetEpisodes(user, seriesId);
        if (episodes is null)
        {
            return Task.FromResult<Guid?>(null);
        }

        int index = episodes.FindIndex(e => e.Id == episodeId);
        if (index < 0)
        {
            return Task.FromResult<Guid?>(null);
        }

        // The other copy of the episode just finished is not "next".
        string currentKey = EpisodeKey(episodes[index]);
        HashSet<string> watched = WatchedKeys(user, episodes);

        var episode = episodes
            .Skip(index + 1)
            .FirstOrDefault(e => EpisodeKey(e) != currentKey && !watched.Contains(EpisodeKey(e)));

        return Task.FromResult(episode?.Id);
    }

    public Task<Guid?> GetFirstUnwatchedEpisodeId(User user, Guid seriesId)
    {
        List<Episode>? episodes = GetEpisodes(user, seriesId);
        if (episodes is null)
        {
            return Task.FromResult<Guid?>(null);
        }

        HashSet<string> watched = WatchedKeys(user, episodes);
        var firstUnwatched = episodes.FirstOrDefault(e => !watched.Contains(EpisodeKey(e)));

        return Task.FromResult(firstUnwatched?.Id);
    }

    public Task<bool> AreAllEarlierEpisodesWatched(User user, Guid seriesId, Guid episodeId)
    {
        List<Episode>? episodes = GetEpisodes(user, seriesId);
        if (episodes is null)
        {
            return Task.FromResult(false);
        }

        int index = episodes.FindIndex(e => e.Id == episodeId);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        // Another copy of the same episode already listed means this is a re-import or the
        // other folder's copy, not a new episode.
        string currentKey = EpisodeKey(episodes[index]);
        if (episodes.Any(e => e.Id != episodeId && EpisodeKey(e) == currentKey))
        {
            return Task.FromResult(false);
        }

        // Specials are often skipped, so an unwatched one shouldn't keep a finished show
        // from coming back.
        var earlierEpisodes = episodes
            .Take(index)
            .Where(e => e.ParentIndexNumber != 0 && EpisodeKey(e) != currentKey)
            .ToList();

        // Nothing earlier means a brand new show, which shouldn't jump into Continue Watching
        // just because its first episode was added to the library.
        if (earlierEpisodes.Count == 0)
        {
            return Task.FromResult(false);
        }

        HashSet<string> watched = WatchedKeys(user, episodes);
        return Task.FromResult(earlierEpisodes.All(e => watched.Contains(EpisodeKey(e))));
    }

    public IReadOnlyList<Guid> GetSeriesGroup(Guid seriesId)
    {
        // With "merge series across folders" each folder of a show is its own Series item
        // (e.g. the disk and the stream copy), all sharing one presentation key.
        string? key = libraryManager.GetItemById(seriesId)?.PresentationUniqueKey;
        if (string.IsNullOrEmpty(key))
        {
            return [seriesId];
        }

        var siblings = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            PresentationUniqueKey = key,
            Recursive = true
        })
            .Select(i => i.Id)
            .Where(id => id != seriesId);

        return [seriesId, .. siblings];
    }

    public Guid? FindOtherCopy(Episode episode)
    {
        if (episode.ParentIndexNumber is not int season
            || episode.IndexNumber is not int number
            || GetShowKey(episode) is not { } showKey)
        {
            return null;
        }

        return libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            SeriesPresentationUniqueKey = showKey,
            ParentIndexNumber = season,
            IndexNumber = number,
            IsVirtualItem = false,
            Recursive = true
        })
            .FirstOrDefault(i => i.Id != episode.Id)?.Id;
    }

    public string? GetShowKey(Episode episode)
    {
        // A freshly added episode can carry no show key yet; its series item has it.
        if (!string.IsNullOrEmpty(episode.SeriesPresentationUniqueKey))
        {
            return episode.SeriesPresentationUniqueKey;
        }

        string? key = episode.SeriesId == Guid.Empty
            ? null
            : libraryManager.GetItemById(episode.SeriesId)?.PresentationUniqueKey;
        return string.IsNullOrEmpty(key) ? null : key;
    }

    private List<Episode>? GetEpisodes(User user, Guid seriesId)
    {
        if (seriesId == Guid.Empty)
        {
            return null;
        }

        // Lists the whole merged show, whichever of its series items this is.
        return libraryManager.GetItemById<Series>(seriesId)?
            .GetEpisodes(user, new DtoOptions(false), false)
            .OfType<Episode>()
            .Where(e => !e.IsMissingEpisode)
            .ToList();
    }

    // A show split across folders lists an episode once per copy. Copies share a key, and an
    // episode counts as watched when any copy is: Jellyfin keeps watched state per item.
    private static string EpisodeKey(Episode episode) =>
        episode.ParentIndexNumber is int season && episode.IndexNumber is int number
            ? $"{season}:{number}"
            : episode.Id.ToString("N");

    private HashSet<string> WatchedKeys(User user, IEnumerable<Episode> episodes) =>
        episodes.Where(e => IsWatched(user, e)).Select(EpisodeKey).ToHashSet();

    private bool IsWatched(User user, BaseItem episode) =>
        userDataManager.GetUserData(user, episode)?.Played ?? false;
}
