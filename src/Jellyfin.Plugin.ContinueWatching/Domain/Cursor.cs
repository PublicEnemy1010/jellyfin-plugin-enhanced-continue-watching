using System;

namespace Jellyfin.Plugin.ContinueWatching.Domain;

public abstract class Cursor
{
    public Guid UserId { get; }
    public Guid ItemId { get; }

    public long PositionTicks { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool Finished { get; private set; }

    /// <summary>
    /// Gets a value indicating whether this instance holds changes that are not in the cursor
    /// store yet. Repositories persist only dirty cursors: a scope that merely *reads* a cursor
    /// must not write its snapshot back, or it would silently revert an update another request
    /// or playback-event handler made in the meantime.
    /// </summary>
    public bool Dirty { get; private set; }

    protected Cursor(
        Guid userId,
        Guid itemId,
        long positionTicks,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        bool dirty)
    {
        UserId = userId;
        ItemId = itemId;
        PositionTicks = positionTicks;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Dirty = dirty;
    }

    protected void UpdatePosition(long positionTicks, DateTimeOffset at)
    {
        PositionTicks = positionTicks;
        UpdatedAt = at;
        Dirty = true;
    }

    protected void SetFinished(bool finished)
    {
        Finished = finished;
        Dirty = true;
    }

    /// <summary>
    /// Called by a repository once this cursor's current state has reached the store.
    /// </summary>
    internal void MarkPersisted() => Dirty = false;
}
