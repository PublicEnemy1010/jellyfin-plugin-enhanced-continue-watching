using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ContinueWatching.Application.Repositories;
using Jellyfin.Plugin.ContinueWatching.Application.Services.SeriesService;
using Jellyfin.Plugin.ContinueWatching.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContinueWatching.Application.Services.CursorService;

public sealed class SeriesCursorPlaybackHandler(
    ISeriesCursorRepository cursorRepository,
    ISeriesService seriesService,
    IUserDataManager userDataManager,
    ILogger<SeriesCursorPlaybackHandler> logger) : ICursorHandler
{
    public bool CanHandle(BaseItem item) => item is Episode;

    public async Task Handle(User user, BaseItem item, PlaybackEvent @event)
    {
        if (item is not Episode episode)
        {
            return;
        }

        // Episodes can be raised through ILibraryManager.ItemAdded before Jellyfin has
        // associated them with a series (notably while creating "Season Unknown").
        // LibraryManager.GetItemById rejects Guid.Empty, so defer handling until a later
        // event rather than allowing an incomplete library item to crash the server.
        if (episode.SeriesId == Guid.Empty)
        {
            logger.LogWarning(
                "Skipping Continue Watching event {EventType} for episode {EpisodeId} because it has no series id yet",
                @event.GetType().Name,
                episode.Id);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (@event is PlaybackStartedEvent)
        {
            ClearPlayedStateForRewatch(user, episode);

            SeriesCursor cursor = await GetOrCreateCursor(
                user,
                episode,
                positionTicks: 0,
                now);

            cursor.StartEpisode(
                episode.Id,
                positionTicks: 0,
                now);

            await cursorRepository.SaveChanges();
            return;
        }

        if (@event is PlaybackProgressEvent progressEvent)
        {
            SeriesCursor cursor = await GetOrCreateCursor(
                user,
                episode,
                progressEvent.PositionTicks,
                now);

            if (cursor.EpisodeId == episode.Id)
            {
                cursor.UpdatePosition(
                    episode.Id,
                    progressEvent.PositionTicks,
                    now);
            }
            else
            {
                cursor.StartEpisode(
                    episode.Id,
                    progressEvent.PositionTicks,
                    now);
            }

            await cursorRepository.SaveChanges();
            return;
        }

        if (@event is PlaybackFinishedEvent)
        {
            var cursor = await GetOrCreateCursor(user, episode, 0, now);

            var nextEpisodeId = await seriesService.GetNextEpisodeId(
                user,
                episode.SeriesId,
                episode.Id);

            cursor.FinishEpisode(nextEpisodeId, now);
            await cursorRepository.SaveChanges();
            return;
        }

        if (@event is ItemMarkedPlayedEvent)
        {
            // If Continue Watching already points at a different episode of this series,
            // marking some other episode played manually shouldn't move the cursor there.
            SeriesCursor? existingCursor = await cursorRepository.TryGet(user.Id, episode.SeriesId);
            if (existingCursor is not null && existingCursor.EpisodeId != episode.Id)
            {
                return;
            }

            // No cursor means the episode is not currently displayed in Continue Watching.
            if (existingCursor is null)
            {
                return;
            }

            var nextEpisodeId = await seriesService.GetNextEpisodeId(
                user,
                episode.SeriesId,
                episode.Id);

            // Manual watched-state changes are explicit user intent: advance to the next
            // unwatched episode, or drop the cursor when there is none.
            existingCursor.FinishEpisode(nextEpisodeId, now);
            await cursorRepository.SaveChanges();
            return;
        }

        if (@event is ItemMarkedUnplayedEvent)
        {
            SeriesCursor? cursor = await cursorRepository.TryGet(user.Id, episode.SeriesId);

            if (cursor is not null && cursor.EpisodeId == episode.Id)
            {
                cursor.UpdatePosition(episode.Id, cursor.PositionTicks, now);
                await cursorRepository.SaveChanges();
                return;
            }

            // Some other, not-currently-tracked episode was marked unwatched (there may be no
            // cursor at all, e.g. the series had been fully watched and removed from Continue
            // Watching). Jump back to whichever episode is now the earliest unwatched one.
            Guid? firstUnwatchedEpisodeId = await seriesService.GetFirstUnwatchedEpisodeId(user, episode.SeriesId);
            if (firstUnwatchedEpisodeId is null)
            {
                return;
            }

            if (cursor is null)
            {
                cursor = SeriesCursor.Create(user.Id, episode.SeriesId, firstUnwatchedEpisodeId.Value, 0, now);
                await cursorRepository.Add(cursor);
            }
            else
            {
                cursor.StartEpisode(firstUnwatchedEpisodeId.Value, 0, now);
            }

            await cursorRepository.SaveChanges();
            return;
        }

        if (@event is NewEpisodeAvailableEvent)
        {
            SeriesCursor? cursor = await cursorRepository.TryGet(user.Id, episode.SeriesId);
            if (cursor is not null)
            {
                // Continue Watching is already showing this series somewhere; leave it alone.
                return;
            }

            bool allOtherEpisodesWatched = await seriesService.AreAllOtherEpisodesWatched(
                user,
                episode.SeriesId,
                episode.Id);

            if (!allOtherEpisodesWatched)
            {
                return;
            }

            cursor = SeriesCursor.Create(user.Id, episode.SeriesId, episode.Id, 0, now);
            await cursorRepository.Add(cursor);
            await cursorRepository.SaveChanges();
        }
    }

    private void ClearPlayedStateForRewatch(User user, BaseItem item)
    {
        UserItemData? data = userDataManager.GetUserData(user, item);
        if (data is null || !data.Played)
        {
            return;
        }

        // Jellyfin doesn't clear the "played" flag just because playback restarted, so a
        // fully-watched episode would otherwise keep its checkmark while Continue Watching
        // shows it as in progress. Whether it ends up unplayed or partially played once
        // playback stops is decided by Jellyfin's own resume-percentage settings from there.
        data.Played = false;
        data.PlaybackPositionTicks = 0;
        userDataManager.SaveUserData(user, item, data, UserDataSaveReason.PlaybackStart, CancellationToken.None);
    }

    private async Task<SeriesCursor> GetOrCreateCursor(
        User user,
        Episode episode,
        long positionTicks,
        DateTimeOffset now)
    {
        SeriesCursor? cursor = await cursorRepository.TryGet(user.Id, episode.SeriesId);
        if (cursor is not null)
        {
            return cursor;
        }

        cursor = SeriesCursor.Create(
            user.Id,
            episode.SeriesId,
            episode.Id,
            positionTicks,
            now);
        await cursorRepository.Add(cursor);
        return cursor;
    }
}
