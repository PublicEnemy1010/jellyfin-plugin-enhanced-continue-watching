using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ContinueWatching.Application.Repositories;
using Jellyfin.Plugin.ContinueWatching.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ContinueWatching.Application.Services.CursorService;

/// <summary>
/// Handles movies and home videos. Jellyfin resolves items inside a "Home Videos" library
/// as plain <see cref="Video"/> instances rather than <see cref="Movie"/>, so both are matched here
/// and tracked/removed the same way.
/// </summary>
public sealed class MovieCursorPlaybackHandler(
    IMovieCursorRepository cursorRepository,
    IUserDataManager userDataManager) : ICursorHandler
{
    public bool CanHandle(BaseItem item) => item is Movie || item.GetType() == typeof(Video);

    public async Task Handle(User user, BaseItem item, PlaybackEvent @event)
    {
        var now = DateTimeOffset.UtcNow;

        if (@event is PlaybackStartedEvent)
        {
            ClearPlayedStateForRewatch(user, item);

            MovieCursor cursor = await GetOrCreateCursor(
                user,
                item,
                positionTicks: 0,
                now);

            cursor.Progress(
                positionTicks: 0,
                now);

            await cursorRepository.SaveChanges();
            return;
        }

        if (@event is PlaybackProgressEvent progressEvent)
        {
            MovieCursor cursor = await GetOrCreateCursor(
                user,
                item,
                progressEvent.PositionTicks,
                now);

            cursor.Progress(
                progressEvent.PositionTicks,
                now);

            await cursorRepository.SaveChanges();
            return;
        }

        if (@event is PlaybackFinishedEvent)
        {
            MovieCursor cursor = await GetOrCreateCursor(
                user,
                item,
                positionTicks: 0,
                now);

            cursor.Finish();
            await cursorRepository.SaveChanges();
            return;
        }

        if (@event is ItemMarkedPlayedEvent)
        {
            MovieCursor? cursor = await cursorRepository.TryGet(user.Id, item.Id);
            if (cursor is null)
            {
                return;
            }

            // A manual watched-state change is explicit user intent. Remove the cursor
            // immediately so a stale progress value cannot be rendered after Jellyfin has
            // already saved Played/PlayCount for the item.
            cursor.Finish();
            await cursorRepository.SaveChanges();
            return;
        }

        if (@event is ItemMarkedUnplayedEvent)
        {
            MovieCursor? cursor = await cursorRepository.TryGet(user.Id, item.Id);

            if (cursor is null)
            {
                return;
            }

            cursor.Progress(cursor.PositionTicks, now);
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
        // fully-watched movie would otherwise keep its checkmark while Continue Watching
        // shows it as in progress. Whether it ends up unplayed or partially played once
        // playback stops is decided by Jellyfin's own resume-percentage settings from there.
        data.Played = false;
        data.PlaybackPositionTicks = 0;
        userDataManager.SaveUserData(user, item, data, UserDataSaveReason.PlaybackStart, CancellationToken.None);
    }

    private async Task<MovieCursor> GetOrCreateCursor(
        User user,
        BaseItem item,
        long positionTicks,
        DateTimeOffset now)
    {
        MovieCursor? cursor = await cursorRepository.TryGet(user.Id, item.Id);
        if (cursor is not null)
        {
            return cursor;
        }

        cursor = MovieCursor.Create(
            user.Id,
            item.Id,
            positionTicks,
            now);
        await cursorRepository.Add(cursor);
        return cursor;
    }
}
