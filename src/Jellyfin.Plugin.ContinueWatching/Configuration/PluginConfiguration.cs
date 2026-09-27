using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ContinueWatching.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // Continue Watching already surfaces each series' next episode, so Jellyfin's own Next Up
    // list only duplicates it. A series' details page keeps its Next Up either way.
    public bool HideNextUp { get; set; } = true;
}
