using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContinueWatching.Application.Repositories;
using Jellyfin.Plugin.ContinueWatching.Domain;
using static Jellyfin.Plugin.ContinueWatching.Infrastructure.Repositories.CursorStore;

namespace Jellyfin.Plugin.ContinueWatching.Infrastructure.Repositories;

public sealed class SeriesCursorRepository(CursorStore cursorStore) : ISeriesCursorRepository
{
    private readonly Dictionary<CursorKey, SeriesCursor> _trackedCursors = [];

    public Task<SeriesCursor?> TryGet(Guid userId, Guid seriesId)
    {
        var key = new CursorKey(userId, seriesId);
        if (_trackedCursors.TryGetValue(key, out SeriesCursor? trackedCursor))
        {
            return trackedCursor.Finished
                ? Task.FromResult<SeriesCursor?>(null)
                : Task.FromResult<SeriesCursor?>(trackedCursor);
        }

        CursorDto? cursorDto = cursorStore.Read(d => d.GetValueOrDefault(key));
        if (cursorDto is null)
        {
            return Task.FromResult<SeriesCursor?>(null);
        }

        SeriesCursor cursor = ToEntity(cursorDto);
        _trackedCursors.Add(key, cursor);
        return Task.FromResult<SeriesCursor?>(cursor);
    }

    public Task Add(SeriesCursor cursor)
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

    private static CursorDto ToDto(SeriesCursor cursor)
    {
        return new CursorDto(
            CursorType.Series,
            cursor.UserId,
            cursor.ItemId,
            cursor.EpisodeId,
            cursor.PositionTicks,
            cursor.CreatedAt,
            cursor.UpdatedAt);
    }

    internal static SeriesCursor ToEntity(CursorDto cursorDto)
    {
        if (cursorDto.Type != CursorType.Series)
        {
            throw new InvalidOperationException("Cursor DTO is not a series cursor.");
        }

        if (cursorDto.EpisodeId is not Guid episodeId)
        {
            throw new InvalidOperationException("Series cursor DTO is missing episode id.");
        }

        return SeriesCursor.Restore(
            cursorDto.UserId,
            cursorDto.ItemId,
            episodeId,
            cursorDto.PositionTicks,
            cursorDto.CreatedAtUtc,
            cursorDto.UpdatedAtUtc);
    }
}