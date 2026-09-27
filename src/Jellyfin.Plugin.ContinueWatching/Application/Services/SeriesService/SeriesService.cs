using System;
using System.Linq;
using System.Threading.Tasks;
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
        if (seriesId == Guid.Empty)
        {
            return Task.FromResult<Guid?>(null);
        }

        var series = libraryManager.GetItemById<Series>(seriesId);

        if (series is null)
        {
            return Task.FromResult<Guid?>(null);
        }

        var options = new DtoOptions(false);

        var episode = series.GetEpisodes(user, options, false)
            .OfType<Episode>()
            .Where(e => !e.IsMissingEpisode)
            .SkipWhile(e => e.Id != episodeId)
            .Skip(1)
            .FirstOrDefault(e => !IsWatched(user, e));

        return Task.FromResult(episode?.Id);
    }

    public Task<Guid?> GetFirstUnwatchedEpisodeId(User user, Guid seriesId)
    {
        if (seriesId == Guid.Empty)
        {
            return Task.FromResult<Guid?>(null);
        }

        var series = libraryManager.GetItemById<Series>(seriesId);

        if (series is null)
        {
            return Task.FromResult<Guid?>(null);
        }

        var options = new DtoOptions(false);

        var firstUnwatched = series.GetEpisodes(user, options, false)
            .OfType<Episode>()
            .Where(e => !e.IsMissingEpisode)
            .FirstOrDefault(e => !IsWatched(user, e));

        return Task.FromResult(firstUnwatched?.Id);
    }

    public Task<bool> AreAllEarlierEpisodesWatched(User user, Guid seriesId, Guid episodeId)
    {
        if (seriesId == Guid.Empty)
        {
            return Task.FromResult(false);
        }

        var series = libraryManager.GetItemById<Series>(seriesId);

        if (series is null)
        {
            return Task.FromResult(false);
        }

        var options = new DtoOptions(false);

        var episodes = series.GetEpisodes(user, options, false)
            .OfType<Episode>()
            .Where(e => !e.IsMissingEpisode)
            .ToList();

        int index = episodes.FindIndex(e => e.Id == episodeId);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        // Specials are often skipped, so an unwatched one shouldn't keep a finished show
        // from coming back.
        var earlierEpisodes = episodes
            .Take(index)
            .Where(e => e.ParentIndexNumber != 0)
            .ToList();

        // Nothing earlier means a brand new show, which shouldn't jump into Continue Watching
        // just because its first episode was added to the library.
        if (earlierEpisodes.Count == 0)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(earlierEpisodes.All(e => IsWatched(user, e)));
    }

    private bool IsWatched(User user, BaseItem episode) =>
        userDataManager.GetUserData(user, episode)?.Played ?? false;
}
