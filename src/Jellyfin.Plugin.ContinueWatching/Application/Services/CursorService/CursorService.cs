using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ContinueWatching.Application.Repositories;
using Jellyfin.Plugin.ContinueWatching.Application.Services.SeriesService;
using Jellyfin.Plugin.ContinueWatching.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContinueWatching.Application.Services.CursorService;

public sealed class CursorService(
    IEnumerable<ICursorHandler> handlers,
    ILibraryManager libraryManager,
    ICursorRepository cursorRepository,
    ISeriesCursorRepository seriesCursorRepository,
    ISeriesService seriesService,
    EpisodeReplacementTracker replacementTracker,
    IUserManager userManager,
    ILogger<CursorService> logger) : ICursorService
{
    public Task OnPlaybackEvent(User user, BaseItem item, PlaybackEvent @event)
    {
        ICursorHandler? handler = handlers.FirstOrDefault(handler => handler.CanHandle(item));
        return handler is null
            ? Task.CompletedTask
            : handler.Handle(user, item, @event);
    }

    public async Task OnItemAdded(BaseItem item)
    {
        ICursorHandler? handler = handlers.FirstOrDefault(handler => handler.CanHandle(item));
        if (handler is null)
        {
            return;
        }

        // A re-imported or second copy of an episode is not a new episode: it must neither
        // resurface a finished show nor leave a cursor on the item it replaced.
        if (item is Episode episode && await HandleReplacement(episode))
        {
            return;
        }

        foreach (User user in userManager.GetUsers())
        {
            await handler.Handle(user, item, NewEpisodeAvailableEvent.Instance);
        }
    }

    public async Task OnItemRemoved(BaseItem item)
    {
        if (item is Episode episode)
        {
            await OnEpisodeRemoved(episode);
            return;
        }

        if (item is Series series && await MoveToOtherSeriesItem(series))
        {
            return;
        }

        if (item.MediaType != MediaType.Video)
            return;

        var result = libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = item.Id,
            Recursive = true,
            MediaTypes = [MediaType.Video]
        }, true)
            .Select(i => i.Id)
            .Union([item.Id])
            .ToHashSet();

        await cursorRepository.DeleteByItemIds(result);
    }

    private async Task OnEpisodeRemoved(Episode episode)
    {
        replacementTracker.RecordRemoved(episode, seriesService.GetShowKey(episode));

        IReadOnlyList<SeriesCursor> cursors = await seriesCursorRepository.GetByEpisodeId(episode.Id);
        if (cursors.Count == 0)
        {
            return;
        }

        // The other copy may already be in the library (a show split across disk and stream
        // folders). If not, the replacement re-points the cursors when it is added.
        if (seriesService.FindOtherCopy(episode) is not { } otherCopyId)
        {
            return;
        }

        foreach (SeriesCursor cursor in cursors)
        {
            cursor.ReplaceEpisode(otherCopyId, keepPosition: true);
        }

        await seriesCursorRepository.SaveChanges();
        logger.LogInformation(
            "Moved {Count} Continue Watching cursor(s) from removed episode {RemovedId} to its other copy {EpisodeId}",
            cursors.Count,
            episode.Id,
            otherCopyId);
    }

    private async Task<bool> HandleReplacement(Episode episode)
    {
        bool isReplacement = false;

        if (replacementTracker.TakeReplaced(episode, seriesService.GetShowKey(episode)) is { } removedId)
        {
            isReplacement = true;
            IReadOnlyList<SeriesCursor> cursors = await seriesCursorRepository.GetByEpisodeId(removedId);
            foreach (SeriesCursor cursor in cursors)
            {
                cursor.ReplaceEpisode(episode.Id, keepPosition: true);
            }

            if (cursors.Count > 0)
            {
                await seriesCursorRepository.SaveChanges();
                logger.LogInformation(
                    "Moved {Count} Continue Watching cursor(s) from removed episode {RemovedId} to its replacement {EpisodeId}",
                    cursors.Count,
                    removedId,
                    episode.Id);
            }
        }

        return isReplacement || seriesService.FindOtherCopy(episode) is not null;
    }

    // When one folder's series item of a merged show leaves the library (its last episode
    // moved to the other folder), keep the cursors on a series item that is still there.
    private async Task<bool> MoveToOtherSeriesItem(Series series)
    {
        Guid? target = seriesService.GetSeriesGroup(series.Id)
            .Where(id => id != series.Id)
            .Cast<Guid?>()
            .FirstOrDefault();
        if (target is not { } targetId)
        {
            return false;
        }

        foreach (SeriesCursor cursor in await seriesCursorRepository.GetBySeriesId(series.Id))
        {
            SeriesCursor? existing = await seriesCursorRepository.TryGet(cursor.UserId, targetId);
            if (existing is not null && existing.UpdatedAt >= cursor.UpdatedAt)
            {
                // The user already has a newer entry for the show under the other item, so
                // drop this one (a finished cursor is deleted on save).
                cursor.FinishEpisode(null, cursor.UpdatedAt);
                continue;
            }

            await seriesCursorRepository.MoveToSeries(cursor, targetId);
        }

        await seriesCursorRepository.SaveChanges();
        return true;
    }
}
