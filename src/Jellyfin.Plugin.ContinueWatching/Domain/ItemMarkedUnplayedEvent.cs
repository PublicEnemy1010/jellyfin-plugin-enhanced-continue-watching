namespace Jellyfin.Plugin.ContinueWatching.Domain;

public sealed record ItemMarkedUnplayedEvent : PlaybackEvent
{
    private ItemMarkedUnplayedEvent() { }

    public static ItemMarkedUnplayedEvent Instance { get; } = new();
}
