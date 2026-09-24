using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContinueWatching.Domain;

namespace Jellyfin.Plugin.ContinueWatching.Application.Repositories;

public interface IMovieCursorRepository
{
    Task<MovieCursor?> TryGet(Guid userId, Guid movieId);

    Task Add(MovieCursor cursor);

    Task SaveChanges();
}