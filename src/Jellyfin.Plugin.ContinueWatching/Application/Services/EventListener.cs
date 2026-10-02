using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContinueWatching.Application.Services.CursorService;
using Jellyfin.Plugin.ContinueWatching.Application.Services.PlayCountService;
using Jellyfin.Plugin.ContinueWatching.Domain;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContinueWatching.Application.Services;

public class EventListener(
    ISessionManager sessionManager,
    ILibraryManager libraryManager,
    IUserDataManager userDataManager,
    IUserManager userManager,
    IServiceScopeFactory scopeFactory,
    ILogger<EventListener> logger) : IHostedService
{
    // How long an episode added without a series id is remembered while waiting for the
    // metadata refresh that fills it in. The refresh normally follows within seconds.
    private static readonly TimeSpan PendingEpisodeLifetime = TimeSpan.FromHours(6);

    // Episodes whose ItemAdded arrived before Jellyfin linked them to a series, keyed by
    // id with the time they were added.
    private readonly ConcurrentDictionary<Guid, DateTime> pendingEpisodes = new();

    private DateTime nextPendingPruneUtc = DateTime.MinValue;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        sessionManager.PlaybackStart += PlaybackStarted;
        sessionManager.PlaybackProgress += PlaybackProgress;
        sessionManager.PlaybackStopped += PlaybackStopped;
        libraryManager.ItemRemoved += ItemRemoved;
        libraryManager.ItemAdded += ItemAdded;
        libraryManager.ItemUpdated += ItemUpdated;
        userDataManager.UserDataSaved += UserDataSaved;
        return Task.CompletedTask;
    }

    private async void PlaybackStarted(object? sender, PlaybackProgressEventArgs args)
    {
        try
        {
            foreach (var user in args.Users)
            {
                using IServiceScope scope = scopeFactory.CreateScope();

                // Playback start is the only thing that raises a play count. Jellyfin's own
                // +1 on completion is undone in UserDataSaved so one viewing counts once.
                scope.ServiceProvider.GetRequiredService<IPlayCountService>()
                    .RecordPlaybackStart(user, args.Item);

                var cursorService = scope.ServiceProvider.GetRequiredService<ICursorService>();
                await cursorService.OnPlaybackEvent(user, args.Item, PlaybackStartedEvent.Instance);
            }
        }
        catch (Exception exception)
        {
            // Exceptions escaping an async-void event callback terminate Jellyfin.
            logger.LogError(exception, "Failed to process playback start for item {ItemId}", args.Item?.Id);
        }
    }

    private async void PlaybackProgress(object? sender, PlaybackProgressEventArgs args)
    {
        try
        {
            // Jellyfin publishes progress events with no position -- a session that is paused
            // before the first tick, or a client that reports state without a timestamp. There
            // is no cursor to advance without one, and dereferencing the null used to kill the
            // whole server from this async-void callback.
            if (args.PlaybackPositionTicks is not { } positionTicks)
            {
                return;
            }

            var @event = new PlaybackProgressEvent(positionTicks);

            foreach (var user in args.Users)
            {
                using IServiceScope scope = scopeFactory.CreateScope();

                var cursorService = scope.ServiceProvider.GetRequiredService<ICursorService>();
                await cursorService.OnPlaybackEvent(user, args.Item, @event);
            }
        }
        catch (Exception exception)
        {
            // Exceptions escaping an async-void event callback terminate Jellyfin.
            logger.LogError(exception, "Failed to process playback progress for item {ItemId}", args.Item?.Id);
        }
    }

    private async void PlaybackStopped(object? sender, PlaybackStopEventArgs args)
    {
        try
        {
            PlaybackEvent? @event;
            if (args.PlayedToCompletion)
            {
                @event = PlaybackFinishedEvent.Instance;
            }
            else if (args.PlaybackPositionTicks is { } positionTicks)
            {
                @event = new PlaybackProgressEvent(positionTicks);
            }
            else
            {
                // Stopped short with no reported position: nothing to record, and the cursor
                // is already where the last progress event left it.
                return;
            }

            foreach (var user in args.Users)
            {
                using IServiceScope scope = scopeFactory.CreateScope();

                var cursorService = scope.ServiceProvider.GetRequiredService<ICursorService>();

                await cursorService.OnPlaybackEvent(user, args.Item, @event);
            }
        }
        catch (Exception exception)
        {
            // Exceptions escaping an async-void event callback terminate Jellyfin.
            logger.LogError(exception, "Failed to process playback stop for item {ItemId}", args.Item?.Id);
        }
    }

    private async void ItemRemoved(object? sender, ItemChangeEventArgs args)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();

            var cursorService = scope.ServiceProvider.GetRequiredService<ICursorService>();

            await cursorService.OnItemRemoved(args.Item);
        }
        catch (Exception exception)
        {
            // Exceptions escaping an async-void event callback terminate Jellyfin.
            logger.LogError(exception, "Failed to process removed library item {ItemId}", args.Item?.Id);
        }
    }

    private async void ItemAdded(object? sender, ItemChangeEventArgs args)
    {
        try
        {
            // Cheap filter before fanning out to every user below -- library scans add lots
            // of non-episode items (movies, songs, images, ...) that this can never apply to.
            if (args.Item is not Episode episode)
            {
                return;
            }

            // Jellyfin raises ItemAdded straight after the first save, and a scanned episode
            // usually has no series id (and may have no season/episode numbers) until the
            // metadata refresh that follows. That refresh raises ItemUpdated, so park the episode
            // and handle it there: without its numbers a second copy can't be told from a new
            // episode.
            if (!IsComplete(episode))
            {
                PrunePendingEpisodes();
                pendingEpisodes[episode.Id] = DateTime.UtcNow;
                logger.LogDebug(
                    "Newly added episode {EpisodeId} has no series id or episode numbers yet; waiting for its metadata refresh",
                    episode.Id);
                return;
            }

            await HandleNewEpisode(episode);
        }
        catch (Exception exception)
        {
            // Exceptions escaping an async-void event callback terminate Jellyfin.
            logger.LogError(exception, "Failed to process added library item {ItemId}", args.Item.Id);
        }
    }

    private async void ItemUpdated(object? sender, ItemChangeEventArgs args)
    {
        try
        {
            // Every library update lands here, so keep the common path to a type check and
            // one dictionary lookup.
            if (args.Item is not Episode episode
                || !IsComplete(episode)
                || !pendingEpisodes.TryRemove(episode.Id, out _))
            {
                return;
            }

            await HandleNewEpisode(episode);
        }
        catch (Exception exception)
        {
            // Exceptions escaping an async-void event callback terminate Jellyfin.
            logger.LogError(exception, "Failed to process updated library item {ItemId}", args.Item?.Id);
        }
    }

    private static bool IsComplete(Episode episode) =>
        episode.SeriesId != Guid.Empty
        && episode.ParentIndexNumber is not null
        && episode.IndexNumber is not null;

    private async Task HandleNewEpisode(Episode episode)
    {
        using IServiceScope scope = scopeFactory.CreateScope();

        var cursorService = scope.ServiceProvider.GetRequiredService<ICursorService>();
        await cursorService.OnItemAdded(episode);
    }

    private void PrunePendingEpisodes()
    {
        DateTime now = DateTime.UtcNow;
        if (now < nextPendingPruneUtc)
        {
            return;
        }

        nextPendingPruneUtc = now.AddMinutes(10);
        foreach (var (episodeId, addedUtc) in pendingEpisodes)
        {
            if (now - addedUtc > PendingEpisodeLifetime)
            {
                pendingEpisodes.TryRemove(episodeId, out _);
            }
        }
    }

    private async void UserDataSaved(object? sender, UserDataSaveEventArgs args)
    {
        try
        {
            // Our own play-count write re-enters here. It is not a user action, and treating it
            // as one would advance a series cursor a second time.
            if (LedgerWrite.IsInProgress)
            {
                return;
            }

            var user = userManager.GetUserById(args.UserId);
            if (user is null)
            {
                return;
            }

            using (IServiceScope playCountScope = scopeFactory.CreateScope())
            {
                playCountScope.ServiceProvider.GetRequiredService<IPlayCountService>()
                    .Reconcile(user, args.Item, args.UserData, args.SaveReason);
            }

            // Jellyfin 12 does not consistently label POST /PlayedItems saves as TogglePlayed.
            // A resulting Played=true state is safe to process for every save reason: playback
            // completion is idempotent with the session event, while manual checkmark saves can
            // no longer be missed. Keep Played=false restricted to TogglePlayed so ordinary
            // playback-progress saves cannot be mistaken for an explicit manual unwatch.
            if (args.SaveReason != UserDataSaveReason.TogglePlayed && !args.UserData.Played)
            {
                return;
            }

            PlaybackEvent @event = args.UserData.Played
                ? ItemMarkedPlayedEvent.Instance
                : ItemMarkedUnplayedEvent.Instance;

            using IServiceScope scope = scopeFactory.CreateScope();

            var cursorService = scope.ServiceProvider.GetRequiredService<ICursorService>();
            await cursorService.OnPlaybackEvent(user, args.Item, @event);
        }
        catch (Exception exception)
        {
            // Exceptions escaping an async-void event callback terminate Jellyfin.
            logger.LogError(exception, "Failed to process user data save for item {ItemId}", args.Item?.Id);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        sessionManager.PlaybackStart -= PlaybackStarted;
        sessionManager.PlaybackProgress -= PlaybackProgress;
        sessionManager.PlaybackStopped -= PlaybackStopped;
        libraryManager.ItemRemoved -= ItemRemoved;
        libraryManager.ItemAdded -= ItemAdded;
        libraryManager.ItemUpdated -= ItemUpdated;
        userDataManager.UserDataSaved -= UserDataSaved;
        return Task.CompletedTask;
    }
}
