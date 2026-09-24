using Jellyfin.Plugin.HomeScreenSections.Client;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System;
using Jellyfin.Plugin.ContinueWatching.Application.Services.Sections;
using Jellyfin.Plugin.ContinueWatching.Web;

namespace Jellyfin.Plugin.ContinueWatching.Tasks;

public sealed class StartupTask(
    ISectionsClient sectionsClient,
    ILogger<StartupTask> logger) : IScheduledTask
{
    public string Name => "ContinueWatching Startup";

    public string Key => "Jellyfin.Plugin.ContinueWatching.Startup";

    public string Description => "Startup service for ContinueWatching";

    public string Category => "Startup services";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // An installed but incompatible Home Screen Sections throws here. Contain it so the
        // File Transformation registration below still runs and the menu button is injected.
        try
        {
            bool registered = sectionsClient.TryRegisterSection<ContinueWatchingSection>(
                new SectionDefinition
                {
                    Id = Guid.Parse("e516c102-407e-449c-b342-04ebf1e89812"),
                    DisplayText = "Continue Watching",
                    Limit = 1,
                });

            if (!registered)
            {
                logger.LogWarning(
                    "Home Screen Sections is unavailable; the Continue Watching section was not registered");
            }
        }
        catch (SectionsException exception)
        {
            logger.LogError(
                exception,
                "Home Screen Sections rejected the Continue Watching section; it was not registered");
        }

        FileTransformationRegistrar.TryRegister(logger);

        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };
    }
}