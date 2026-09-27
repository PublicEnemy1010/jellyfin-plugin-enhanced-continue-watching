using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.ContinueWatching.Application.Services.SeriesService;

public interface ISeriesService
{
    /// <summary>
    /// Returns the id of the earliest unwatched episode after <paramref name="episodeId"/> in
    /// series order, or <c>null</c> if there is no such episode (e.g. it was the last one, or
    /// every later episode has already been watched).
    /// </summary>
    Task<Guid?> GetNextEpisodeId(User user, Guid seriesId, Guid episodeId);

    /// <summary>
    /// Returns the id of the earliest (in series order) episode the user has not watched, or
    /// <c>null</c> if the series has no episodes or the user has watched all of them.
    /// </summary>
    Task<Guid?> GetFirstUnwatchedEpisodeId(User user, Guid seriesId);

    /// <summary>
    /// Returns whether the user has watched every episode that comes before
    /// <paramref name="episodeId"/> in series order, ignoring specials. <c>false</c> when there
    /// is no earlier episode, or when the episode isn't in the series.
    /// </summary>
    Task<bool> AreAllEarlierEpisodesWatched(User user, Guid seriesId, Guid episodeId);
}