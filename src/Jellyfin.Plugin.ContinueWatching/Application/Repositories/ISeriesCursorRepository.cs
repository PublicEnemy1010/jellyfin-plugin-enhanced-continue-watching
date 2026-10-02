using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContinueWatching.Domain;

namespace Jellyfin.Plugin.ContinueWatching.Application.Repositories;

public interface ISeriesCursorRepository
{
    Task<SeriesCursor?> TryGet(Guid userId, Guid seriesId);

    /// <summary>
    /// Returns every user's unfinished cursor that currently points at
    /// <paramref name="episodeId"/>, tracked like <see cref="TryGet"/>.
    /// </summary>
    Task<IReadOnlyList<SeriesCursor>> GetByEpisodeId(Guid episodeId);

    /// <summary>
    /// Returns every user's unfinished cursor keyed by <paramref name="seriesId"/>.
    /// </summary>
    Task<IReadOnlyList<SeriesCursor>> GetBySeriesId(Guid seriesId);

    Task Add(SeriesCursor cursor);

    /// <summary>
    /// Re-keys <paramref name="cursor"/> to <paramref name="seriesId"/>. The old key leaves the
    /// store at once; the moved cursor is written by <see cref="SaveChanges"/>.
    /// </summary>
    Task MoveToSeries(SeriesCursor cursor, Guid seriesId);

    Task SaveChanges();
}