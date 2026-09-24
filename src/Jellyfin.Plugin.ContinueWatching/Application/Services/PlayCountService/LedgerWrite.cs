using System;
using System.Threading;

namespace Jellyfin.Plugin.ContinueWatching.Application.Services.PlayCountService;

/// <summary>
/// Marks the current flow as "this save is the plugin writing its own ledger value back".
/// <para>
/// Saving user data raises <c>UserDataSaved</c>, so the play-count restore re-enters the event
/// handler. Without this marker that re-entry looks like a fresh mark-watched and advances a
/// series cursor a second time. Jellyfin raises the event synchronously inside
/// <c>SaveUserData</c>, so an <see cref="AsyncLocal{T}"/> set around the call is visible to the
/// handler.
/// </para>
/// </summary>
internal static class LedgerWrite
{
    private static readonly AsyncLocal<bool> InProgress = new();

    internal static bool IsInProgress => InProgress.Value;

    internal static IDisposable Begin()
    {
        InProgress.Value = true;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => InProgress.Value = false;
    }
}
