using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContinueWatching.Application.Repositories;
using Jellyfin.Plugin.ContinueWatching.Application.Services.CursorService;
using Jellyfin.Plugin.ContinueWatching.Application.Services.Sections;
using Jellyfin.Plugin.ContinueWatching.Domain;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ContinueWatching.Controllers;

[ApiController]
[Authorize]
[Route("")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ContinueWatchingController(
    ContinueWatchingSection continueWatchingSection,
    ICursorRepository cursorRepository,
    IUserManager userManager,
    IUserDataManager userDataManager,
    ILibraryManager libraryManager,
    ICursorService cursorService) : ControllerBase
{
    private const string UserIdClaimType = "Jellyfin-UserId";
    private const string AdministratorRole = "Administrator";

    [HttpGet("UserItems/Resume")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetResumeItemsAsync(
        [FromQuery] Guid? userId,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery] string? searchTerm,
        [FromQuery] Guid? parentId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] MediaType[] mediaTypes,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] excludeItemTypes,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] includeItemTypes,
        [FromQuery] bool enableTotalRecordCount = true,
        [FromQuery] bool? enableImages = true,
        [FromQuery] bool excludeActiveSessions = false)
    {
        var requestUserId = GetUserId(userId);
        var user = userManager.GetUserById(requestUserId);

        if (user is null)
        {
            return NotFound();
        }

        return await continueWatchingSection.GetItemsAsync(
            requestUserId,
            startIndex,
            limit,
            searchTerm,
            parentId,
            fields,
            mediaTypes,
            enableUserData,
            imageTypeLimit,
            enableImageTypes,
            excludeItemTypes,
            includeItemTypes,
            enableTotalRecordCount,
            enableImages,
            excludeActiveSessions);
    }

    [HttpGet("Users/{userId}/Items/Resume")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<QueryResult<BaseItemDto>>> GetResumeItemsLegacy(
        [FromRoute, Required] Guid userId,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery] string? searchTerm,
        [FromQuery] Guid? parentId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] MediaType[] mediaTypes,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] excludeItemTypes,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] includeItemTypes,
        [FromQuery] bool enableTotalRecordCount = true,
        [FromQuery] bool? enableImages = true,
        [FromQuery] bool excludeActiveSessions = false)
        => GetResumeItemsAsync(
                userId,
                startIndex,
                limit,
                searchTerm,
                parentId,
                fields,
                mediaTypes,
                enableUserData,
                imageTypeLimit,
                enableImageTypes,
                excludeItemTypes,
                includeItemTypes,
                enableTotalRecordCount,
                enableImages,
                excludeActiveSessions);

    [HttpDelete("ContinueWatching/{itemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> RemoveItemAsync([FromRoute, Required] Guid itemId, [FromQuery] Guid? userId)
        => RemoveItemInternalAsync(itemId, userId);

    // Mirrors RemoveItemAsync but over POST: some reverse proxies/WAFs in front of a
    // Jellyfin instance block DELETE requests outright, which would otherwise make the
    // injected "Remove from Continue Watching" menu button silently fail for those setups.
    [HttpPost("ContinueWatching/{itemId}/remove")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> RemoveItemViaPostAsync([FromRoute, Required] Guid itemId, [FromQuery] Guid? userId)
        => RemoveItemInternalAsync(itemId, userId);

    // Jellyfin does not reliably publish UserDataSaved after its web client's PlayedItems
    // request. The injected home-page integration calls this only after it has independently
    // confirmed the persisted Played state, giving the cursor an explicit, authenticated
    // notification without replacing Jellyfin's own watched-state API. Jellyfin can leave the
    // old resume position in UserItemData when marking an in-progress item played, so normalize
    // that position here as well; otherwise library cards can continue to render (for example)
    // "50% played" after the Continue Watching cursor has already advanced.
    [HttpPost("ContinueWatching/{itemId}/played")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ItemMarkedPlayedAsync(
        [FromRoute, Required] Guid itemId,
        [FromQuery] Guid? userId)
    {
        var requestUserId = GetUserId(userId);
        var user = userManager.GetUserById(requestUserId);
        var item = libraryManager.GetItemById(itemId);

        // Same 404 for a hidden item as for a missing one, so the endpoint can't be used to
        // probe for items outside the user's libraries or parental rating.
        if (user is null || item is null || !item.IsVisible(user))
        {
            return NotFound();
        }

        var userData = userDataManager.GetUserData(user, item);
        if (userData is not null && userData.PlaybackPositionTicks != 0)
        {
            userData.PlaybackPositionTicks = 0;
            userDataManager.SaveUserData(
                user,
                item,
                userData,
                UserDataSaveReason.UpdateUserData,
                CancellationToken.None);
        }

        await cursorService.OnPlaybackEvent(user, item, ItemMarkedPlayedEvent.Instance);
        return NoContent();
    }

    private async Task<IActionResult> RemoveItemInternalAsync(Guid itemId, Guid? userId)
    {
        var requestUserId = GetUserId(userId);
        var user = userManager.GetUserById(requestUserId);

        if (user is null)
        {
            return NotFound();
        }

        bool removed = await cursorRepository.DeleteByDisplayItemId(requestUserId, itemId);
        return removed ? NoContent() : NotFound();
    }

    private Guid GetUserId(Guid? userId)
    {
        string? value = User.Claims
            .FirstOrDefault(claim => claim.Type.Equals(UserIdClaimType, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (!Guid.TryParse(value, out Guid authenticatedUserId))
        {
            authenticatedUserId = default;
        }

        if (userId is null)
        {
            return authenticatedUserId;
        }

        var isAdministrator = User.IsInRole(AdministratorRole);
        return !userId.Equals(authenticatedUserId) && !isAdministrator ? throw new SecurityException("Forbidden") : userId.Value;
    }
}
