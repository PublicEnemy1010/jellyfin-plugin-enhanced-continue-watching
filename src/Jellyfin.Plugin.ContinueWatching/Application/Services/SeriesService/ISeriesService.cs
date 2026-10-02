using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities.TV;

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
    /// is no earlier episode, when the episode isn't in the series, or when another copy of it
    /// is listed.
    /// </summary>
    Task<bool> AreAllEarlierEpisodesWatched(User user, Guid seriesId, Guid episodeId);

    /// <summary>
    /// Returns <paramref name="seriesId"/> followed by the other series items of the same show
    /// (one per library folder when Jellyfin merges series across folders).
    /// </summary>
    IReadOnlyList<Guid> GetSeriesGroup(Guid seriesId);

    /// <summary>
    /// Returns another library item for the same show, season and episode number as
    /// <paramref name="episode"/> (a second copy, or what replaced it), or <c>null</c>.
    /// </summary>
    Guid? FindOtherCopy(Episode episode);

    /// <summary>
    /// Returns the show's presentation key for <paramref name="episode"/>, shared by every
    /// series item of a merged show, falling back to its series item while the episode has none.
    /// </summary>
    string? GetShowKey(Episode episode);
}