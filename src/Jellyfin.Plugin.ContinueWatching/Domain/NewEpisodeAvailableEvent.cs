namespace Jellyfin.Plugin.ContinueWatching.Domain;

public sealed record NewEpisodeAvailableEvent : PlaybackEvent
{
    private NewEpisodeAvailableEvent() { }

    public static NewEpisodeAvailableEvent Instance { get; } = new();
}
