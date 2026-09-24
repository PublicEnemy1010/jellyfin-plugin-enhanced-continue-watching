using System;
using System.IO;
using System.Reflection;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ContinueWatching;

public class Plugin : IPlugin
{
    public Guid Id { get; } = Guid.Parse("7dac1912-44c9-485a-a7bd-df8abc1bc9d3");

    public string Name => "Enhanced Continue Watching";

    public string Description => "Continue Watching updates Jellyfin's default resume list to work like you expect it to.";

    public Version Version { get; } = typeof(Plugin).Assembly.GetName().Version!;

    public string AssemblyFilePath { get; } = typeof(Plugin).Assembly.Location;

    public bool CanUninstall => !string.Equals(
        Path.GetDirectoryName(AssemblyFilePath),
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
        StringComparison.Ordinal);

    public string DataFolderPath { get; } = string.Empty;

    public PluginInfo GetPluginInfo() => new(Name, Version, Description, Id, CanUninstall);

    public void OnUninstalling() { }
}