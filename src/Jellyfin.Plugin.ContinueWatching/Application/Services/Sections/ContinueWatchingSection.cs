using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using Jellyfin.Plugin.ContinueWatching.Application.Repositories;
using Jellyfin.Plugin.ContinueWatching.Application.Services.SeriesService;
using Jellyfin.Plugin.ContinueWatching.Domain;
using Jellyfin.Plugin.HomeScreenSections.Client;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.ContinueWatching.Application.Services.Sections;

public sealed class ContinueWatchingSection(
    ICursorRepository cursorRepository,
    ISeriesCursorRepository seriesCursorRepository,
    IMovieCursorRepository movieCursorRepository,
    ISeriesService seriesService,
    IUserManager userManager,
    IUserDataManager userDataManager,
    ILibraryManager libraryManager,
    IDtoService dtoService,
    ISessionManager sessionManager) : ISectionResultsProvider
{

    public QueryResult<BaseItemDto> GetResults(SectionRequest request)
    {
        var result = GetItemsAsync(
            request.UserId,
            startIndex: null,
            limit: 12,
            searchTerm: null,
            parentId: null,
            fields: [ItemFields.PrimaryImageAspectRatio],
            mediaTypes: [MediaType.Video],
            enableUserData: true,
            imageTypeLimit: 1,
            enableImageTypes: [ImageType.Primary, ImageType.Backdrop, ImageType.Thumb],
            excludeItemTypes: [],
            includeItemTypes: [],
            enableTotalRecordCount: false);

        result.Wait();
        return result.Result;
    }

    public async Task<QueryResult<BaseItemDto>> GetItemsAsync(
        Guid userId,
        int? startIndex,
        int? limit,
        string? searchTerm,
        Guid? parentId,
        ItemFields[] fields,
        MediaType[] mediaTypes,
        bool? enableUserData,
        int? imageTypeLimit,
        ImageType[] enableImageTypes,
        BaseItemKind[] excludeItemTypes,
        BaseItemKind[] includeItemTypes,
        bool enableTotalRecordCount = true,
        bool? enableImages = true,
        bool excludeActiveSessions = false,
        string? client = null)
    {
        var user = userManager.GetUserById(userId);
        if (user is null)
        {
            return new QueryResult<BaseItemDto>([]);
        }

        var cursors = await cursorRepository.GetByUserId(userId);
        if (await ReconcilePlayedCursors(user, cursors))
        {
            cursors = await cursorRepository.GetByUserId(userId);
        }

        if (cursors.Count == 0)
        {
            return new QueryResult<BaseItemDto>(startIndex, 0, []);
        }

        var parentIdGuid = parentId ?? Guid.Empty;
        var dtoOptions = new DtoOptions { Fields = fields }
            .AddClientFields(client)
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);

        var ancestorIds = Array.Empty<Guid>();

        var excludeFolderIds = user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes);
        if (parentIdGuid.IsEmpty() && excludeFolderIds.Length > 0)
        {
            ancestorIds = [.. libraryManager.GetUserRootFolder().GetChildren(user, true)
                .Where(i => i is Folder && !excludeFolderIds.Contains(i.Id))
                .Select(i => i.Id)];
        }

        var excludeItemIds = Array.Empty<Guid>();
        if (excludeActiveSessions)
        {
            excludeItemIds = [.. sessionManager.Sessions
                .Where(s => s.UserId.Equals(userId) && s.NowPlayingItem is not null)
                .Select(s => s.NowPlayingItem.Id)];
        }

        // Deliberately unpaged: the library cannot sort by Continue Watching recency, so letting
        // it apply StartIndex/Limit here would pick an arbitrary page by its own ordering and the
        // most recently watched item could be missing entirely. Paging is applied after the sort.
        QueryResult<BaseItem> itemsResult = libraryManager.GetItemsResult(
            new InternalItemsQuery(user)
            {
                ParentId = Guid.Empty,
                Recursive = true,
                DtoOptions = dtoOptions,
                MediaTypes = mediaTypes ?? Enum.GetValues<MediaType>(),
                IsVirtualItem = false,
                CollapseBoxSetItems = false,
                EnableTotalRecordCount = false,
                AncestorIds = ancestorIds,
                IncludeItemTypes = includeItemTypes ?? [],
                ExcludeItemTypes = excludeItemTypes ?? [],
                ExcludeItemIds = excludeItemIds,
                SearchTerm = searchTerm,
                ItemIds = [.. cursors.Select(GetItemId)]
            });

        var cursorByItemId = cursors.ToDictionary(GetItemId);

        var ordered = dtoService.GetBaseItemDtos(itemsResult.Items, dtoOptions, user)
            .Select(i => (Item: i, Cursor: cursorByItemId.GetValueOrDefault(i.Id)))
            .Where(p => p.Cursor is not null)
            .Cast<(BaseItemDto Item, Cursor Cursor)>()
            .OrderByDescending(CursorUpdatedAt)
            .Select(UpdateUserData)
            .Select(p => p.Item)
            .ToList();

        IEnumerable<BaseItemDto> page = ordered.Skip(startIndex ?? 0);
        if (limit is int pageSize)
        {
            page = page.Take(pageSize);
        }

        var results = page.ToList();

        (BaseItemDto Item, Cursor Cursor) UpdateUserData((BaseItemDto Item, Cursor Cursor) arg)
        {
            if (enableUserData is false or null)
            {
                return arg;
            }

            arg.Item.UserData ??= new UserItemDataDto
            {
                Key = arg.Item.Id.ToString()
            };
            arg.Item.UserData.PlaybackPositionTicks = arg.Cursor.PositionTicks;
            if (arg.Item.RunTimeTicks is > 0)
            {
                arg.Item.UserData.PlayedPercentage = (double)arg.Cursor.PositionTicks / arg.Item.RunTimeTicks * 100;
            }

            return arg;
        }

        static DateTimeOffset CursorUpdatedAt((BaseItemDto Item, Cursor Cursor) arg)
        {
            return arg.Cursor.UpdatedAt;
        }

        return new QueryResult<BaseItemDto>(
            startIndex,
            enableTotalRecordCount ? ordered.Count : 0,
            results);
    }

    private async Task<bool> ReconcilePlayedCursors(User user, IReadOnlyCollection<Cursor> cursors)
    {
        bool seriesChanged = false;
        bool movieChanged = false;

        foreach (Cursor cursor in cursors)
        {
            Guid displayItemId = cursor switch
            {
                SeriesCursor seriesCursor => seriesCursor.EpisodeId,
                MovieCursor movieCursor => movieCursor.ItemId,
                _ => Guid.Empty
            };

            if (displayItemId == Guid.Empty)
            {
                continue;
            }

            BaseItem? displayItem = libraryManager.GetItemById(displayItemId);
            if (displayItem is null || !(userDataManager.GetUserData(user, displayItem)?.Played ?? false))
            {
                continue;
            }

            if (cursor is SeriesCursor)
            {
                SeriesCursor? trackedCursor = await seriesCursorRepository.TryGet(user.Id, cursor.ItemId);
                if (trackedCursor is null)
                {
                    continue;
                }

                Guid? nextEpisodeId = await seriesService.GetNextEpisodeId(
                    user,
                    cursor.ItemId,
                    displayItemId);

                trackedCursor.FinishEpisode(nextEpisodeId, DateTimeOffset.UtcNow);
                seriesChanged = true;
            }
            else if (cursor is MovieCursor)
            {
                MovieCursor? trackedCursor = await movieCursorRepository.TryGet(user.Id, cursor.ItemId);
                if (trackedCursor is null)
                {
                    continue;
                }

                trackedCursor.Finish();
                movieChanged = true;
            }
        }

        if (seriesChanged)
        {
            await seriesCursorRepository.SaveChanges();
        }

        if (movieChanged)
        {
            await movieCursorRepository.SaveChanges();
        }

        return seriesChanged || movieChanged;
    }

    private static Guid GetItemId(Cursor cursor)
    {
        return cursor switch
        {
            SeriesCursor sc => sc.EpisodeId,
            MovieCursor mc => mc.ItemId,
            _ => throw new NotImplementedException()
        };
    }
}
