using System;
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
    public Task StartAsync(CancellationToken cancellationToken)
    {
        sessionManager.PlaybackStart += PlaybackStarted;
        sessionManager.PlaybackProgress += PlaybackProgress;
        sessionManager.PlaybackStopped += PlaybackStopped;
        libraryManager.ItemRemoved += ItemRemoved;
        libraryManager.ItemAdded += ItemAdded;
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

            // Jellyfin may publish ItemAdded while an episode is only partially populated;
            // there is no valid series cursor to update until the relationship exists.
            if (episode.SeriesId == Guid.Empty)
            {
                logger.LogWarning(
                    "Skipping newly added episode {EpisodeId} because it has no series id yet",
                    episode.Id);
                return;
            }

            using IServiceScope scope = scopeFactory.CreateScope();

            var cursorService = scope.ServiceProvider.GetRequiredService<ICursorService>();
            await cursorService.OnItemAdded(args.Item);
        }
        catch (Exception exception)
        {
            // Exceptions escaping an async-void event callback terminate Jellyfin.
            logger.LogError(exception, "Failed to process added library item {ItemId}", args.Item.Id);
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
        userDataManager.UserDataSaved -= UserDataSaved;
        return Task.CompletedTask;
    }
}
