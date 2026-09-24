using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContinueWatching.Domain;

namespace Jellyfin.Plugin.ContinueWatching.Application.Repositories;

public interface ISeriesCursorRepository
{
    Task<SeriesCursor?> TryGet(Guid userId, Guid seriesId);

    Task Add(SeriesCursor cursor);

    Task SaveChanges();
}