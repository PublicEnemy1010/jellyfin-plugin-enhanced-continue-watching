using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContinueWatching.Application.Repositories;
using Jellyfin.Plugin.ContinueWatching.Domain;
using static Jellyfin.Plugin.ContinueWatching.Infrastructure.Repositories.CursorStore;

namespace Jellyfin.Plugin.ContinueWatching.Infrastructure.Repositories;

public sealed class MovieCursorRepository(CursorStore cursorStore) : IMovieCursorRepository
{
    private readonly Dictionary<CursorKey, MovieCursor> _trackedCursors = [];

    public Task<MovieCursor?> TryGet(Guid userId, Guid movieId)
    {
        var key = new CursorKey(userId, movieId);
        if (_trackedCursors.TryGetValue(key, out MovieCursor? trackedCursor))
        {
            return trackedCursor.Finished
                ? Task.FromResult<MovieCursor?>(null)
                : Task.FromResult<MovieCursor?>(trackedCursor);
        }

        CursorDto? cursorDto = cursorStore.Read(d => d.GetValueOrDefault(key));
        if (cursorDto is null)
        {
            return Task.FromResult<MovieCursor?>(null);
        }

        MovieCursor cursor = ToEntity(cursorDto);
        _trackedCursors.Add(key, cursor);
        return Task.FromResult<MovieCursor?>(cursor);
    }

    public Task Add(MovieCursor cursor)
    {
        _trackedCursors[new CursorKey(cursor.UserId, cursor.ItemId)] = cursor;

        return Task.CompletedTask;
    }

    public Task SaveChanges()
    {
        foreach (var (key, cursor) in _trackedCursors)
        {
            // Only cursors this scope actually changed may be written back. Playback events and
            // Continue Watching reads run concurrently in separate scopes over the same store,
            // so persisting an untouched snapshot here would revert whichever update landed
            // while this scope was working -- the stale-card bug that reload could not fix.
            if (!cursor.Dirty)
            {
                continue;
            }

            if (cursor.Finished)
            {
                cursorStore.Delete(key);
            }
            else
            {
                cursorStore.Upsert(key, ToDto(cursor));
            }

            cursor.MarkPersisted();
        }

        return Task.CompletedTask;
    }

    private static CursorDto ToDto(MovieCursor cursor)
    {
        return new CursorDto(
            CursorType.Movie,
            cursor.UserId,
            cursor.ItemId,
            null,
            cursor.PositionTicks,
            cursor.CreatedAt,
            cursor.UpdatedAt);
    }

    internal static MovieCursor ToEntity(CursorDto cursorDto)
    {
        if (cursorDto.Type != CursorType.Movie)
        {
            throw new InvalidOperationException("Cursor DTO is not a movie cursor.");
        }

        return MovieCursor.Restore(
            cursorDto.UserId,
            cursorDto.ItemId,
            cursorDto.PositionTicks,
            cursorDto.CreatedAtUtc,
            cursorDto.UpdatedAtUtc);
    }
}