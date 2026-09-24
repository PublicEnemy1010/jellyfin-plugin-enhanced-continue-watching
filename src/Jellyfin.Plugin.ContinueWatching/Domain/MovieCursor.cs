using System;

namespace Jellyfin.Plugin.ContinueWatching.Domain;

public sealed class MovieCursor : Cursor
{
    private MovieCursor(
        Guid userId,
        Guid itemId,
        long positionTicks,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        bool dirty) : base(userId, itemId, positionTicks, createdAt, updatedAt, dirty)
    {
    }

    // A freshly created cursor is not in the store yet, so it always needs persisting.
    public static MovieCursor Create(Guid userId, Guid movieId, long positionTicks, DateTimeOffset at) =>
        new(userId, movieId, positionTicks, at, at, dirty: true);

    internal static MovieCursor Restore(Guid userId, Guid movieId, long positionTicks, DateTimeOffset createdAt, DateTimeOffset updatedAt) =>
        new(userId, movieId, positionTicks, createdAt, updatedAt, dirty: false);

    public void Progress(long positionTicks, DateTimeOffset at)
    {
        UpdatePosition(positionTicks, at);
    }

    public void Finish()
    {
        SetFinished(true);
    }
}
