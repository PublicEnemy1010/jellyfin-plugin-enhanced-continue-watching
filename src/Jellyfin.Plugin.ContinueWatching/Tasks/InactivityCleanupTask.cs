using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContinueWatching.Infrastructure.Repositories;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContinueWatching.Tasks;

/// <summary>
/// Drops Continue Watching entries nobody has touched in <see cref="InactivityThreshold"/>.
/// Deleting the cursor is the same thing "Remove from Continue Watching" does: playing the
/// title again brings it back, and a fully watched series still resurfaces on a new episode.
/// </summary>
public sealed class InactivityCleanupTask(
    CursorStore cursorStore,
    ILogger<InactivityCleanupTask> logger) : IScheduledTask
{
    private static readonly TimeSpan InactivityThreshold = TimeSpan.FromDays(180);

    public string Name => "Clean up inactive Continue Watching entries";

    public string Key => "Jellyfin.Plugin.ContinueWatching.InactivityCleanup";

    public string Description =>
        $"Removes Continue Watching entries that have not been played in {InactivityThreshold.TotalDays:0} days.";

    public string Category => "Continue Watching";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int removed = cursorStore.DeleteUpdatedBefore(DateTimeOffset.UtcNow - InactivityThreshold);
        logger.LogInformation(
            "Removed {Count} Continue Watching entries inactive for more than {Days} days",
            removed,
            InactivityThreshold.TotalDays);

        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
        };
    }
}
