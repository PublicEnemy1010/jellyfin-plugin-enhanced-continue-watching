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

    public Task<bool> AreAllOtherEpisodesWatched(User user, Guid seriesId, Guid exceptEpisodeId)
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

        var otherEpisodes = series.GetEpisodes(user, options, false)
            .OfType<Episode>()
            .Where(e => !e.IsMissingEpisode && e.Id != exceptEpisodeId)
            .ToList();

        // A series with no other episodes isn't a "fully watched show" gaining a new
        // episode -- it's a brand new show, which shouldn't jump into Continue Watching
        // just because its first episode was added to the library.
        if (otherEpisodes.Count == 0)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(otherEpisodes.All(e => IsWatched(user, e)));
    }

    private bool IsWatched(User user, BaseItem episode) =>
        userDataManager.GetUserData(user, episode)?.Played ?? false;
}
