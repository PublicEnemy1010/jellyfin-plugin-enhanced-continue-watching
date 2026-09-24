using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContinueWatching.Domain;

namespace Jellyfin.Plugin.ContinueWatching.Application.Repositories;

public interface ICursorRepository
{
    Task<IReadOnlyCollection<Cursor>> GetByUserId(Guid userId);

    Task DeleteByItemIds(IReadOnlySet<Guid> ids);

    /// <summary>
    /// Removes whatever Continue Watching entry is showing the given item for this user --
    /// for a series cursor, <paramref name="displayItemId"/> is the currently-tracked episode
    /// id (what clients actually see as the item's id), not the series id.
    /// </summary>
    /// <returns><c>true</c> if an entry was found and removed.</returns>
    Task<bool> DeleteByDisplayItemId(Guid userId, Guid displayItemId);
}