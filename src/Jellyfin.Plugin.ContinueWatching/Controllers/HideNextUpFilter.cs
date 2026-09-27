using System;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.ContinueWatching.Controllers;

// Answers Jellyfin's GET /Shows/NextUp with an empty list while HideNextUp is on. Clients hide an
// empty Next Up section. Requests scoped to one series pass through, so a series' details page
// still shows its next episode.
public sealed class HideNextUpFilter : IAsyncActionFilter
{
    private const string SeriesIdArgument = "seriesId";

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        bool hideNextUp = Plugin.Instance?.Configuration.HideNextUp ?? false;
        bool seriesScoped = context.ActionArguments.TryGetValue(SeriesIdArgument, out object? seriesId)
            && seriesId is Guid id
            && id != Guid.Empty;

        if (!hideNextUp || seriesScoped)
        {
            return next();
        }

        context.Result = new OkObjectResult(new QueryResult<BaseItemDto>());
        return Task.CompletedTask;
    }
}
