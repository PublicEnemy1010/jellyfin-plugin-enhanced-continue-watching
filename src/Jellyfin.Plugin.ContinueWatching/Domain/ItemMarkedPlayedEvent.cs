namespace Jellyfin.Plugin.ContinueWatching.Domain;

public sealed record ItemMarkedPlayedEvent : PlaybackEvent
{
    private ItemMarkedPlayedEvent() { }

    public static ItemMarkedPlayedEvent Instance { get; } = new();
}
