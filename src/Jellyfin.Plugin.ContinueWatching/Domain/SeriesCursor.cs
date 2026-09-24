using System;

namespace Jellyfin.Plugin.ContinueWatching.Domain;

public sealed class SeriesCursor : Cursor
{
    public Guid EpisodeId { get; private set; }

    private SeriesCursor(
        Guid userId,
        Guid itemId,
        long positionTicks,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        bool dirty) : base(userId, itemId, positionTicks, createdAt, updatedAt, dirty)
    {
    }

    public static SeriesCursor Create(
        Guid userId,
        Guid seriesId,
        Guid episodeId,
        long positionTicks,
        DateTimeOffset at)
    {
        // A freshly created cursor is not in the store yet, so it always needs persisting --
        // some callers add it without mutating it further.
        return new SeriesCursor(userId, seriesId, positionTicks, at, at, dirty: true)
        {
            EpisodeId = episodeId
        };
    }

    internal static SeriesCursor Restore(
        Guid userId,
        Guid seriesId,
        Guid episodeId,
        long positionTicks,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        return new SeriesCursor(userId, seriesId, positionTicks, createdAt, updatedAt, dirty: false)
        {
            EpisodeId = episodeId
        };
    }

    public void StartEpisode(
        Guid episodeId,
        long positionTicks,
        DateTimeOffset at)
    {
        EpisodeId = episodeId;
        UpdatePosition(positionTicks, at);
    }

    public void UpdatePosition(
        Guid episodeId,
        long positionTicks,
        DateTimeOffset at)
    {
        if (episodeId != EpisodeId)
        {
            return;
        }

        UpdatePosition(positionTicks, at);
    }

    public void FinishEpisode(
        Guid? nextEpisodeId,
        DateTimeOffset at)
    {
        if (nextEpisodeId is null)
        {
            UpdatePosition(0, at);
            SetFinished(true);
            return;
        }

        EpisodeId = nextEpisodeId.Value;
        UpdatePosition(0, at);
        SetFinished(false);
    }
}
