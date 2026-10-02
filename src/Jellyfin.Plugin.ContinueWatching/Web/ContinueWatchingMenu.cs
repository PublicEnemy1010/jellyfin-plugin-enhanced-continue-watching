using System;
using System.IO;

namespace Jellyfin.Plugin.ContinueWatching.Web;

public sealed class PatchRequestPayload
{
    public string? Contents { get; set; }
}

public static class ContinueWatchingMenuTransformation
{
    private const string ScriptOpenMarker = "<script data-continue-watching-remove=\"true\">";
    private const string ScriptCloseMarker = "</script>";

    public static string IndexTransformation(PatchRequestPayload payload)
    {
        string contents = payload.Contents ?? string.Empty;
        if (InjectedScript is null)
        {
            return contents;
        }

        int existingStart = contents.IndexOf(ScriptOpenMarker, StringComparison.Ordinal);
        if (existingStart >= 0)
        {
            int existingEnd = contents.IndexOf(ScriptCloseMarker, existingStart, StringComparison.Ordinal);
            if (existingEnd >= 0)
            {
                contents = contents.Remove(existingStart, existingEnd + ScriptCloseMarker.Length - existingStart);
            }
        }

        const string ClosingBody = "</body>";
        int bodyIndex = contents.LastIndexOf(ClosingBody, StringComparison.OrdinalIgnoreCase);
        if (bodyIndex < 0)
        {
            return contents;
        }

        string script = ScriptOpenMarker + "\n" + InjectedScript + ScriptCloseMarker;
        return contents.Insert(bodyIndex, script);
    }

    // Kept in continueWatchingMenu.js (an embedded resource) so it can be edited as JavaScript.
    private static readonly string? InjectedScript = LoadInjectedScript();

    private static string? LoadInjectedScript()
    {
        Type type = typeof(ContinueWatchingMenuTransformation);
        using Stream? stream = type.Assembly.GetManifestResourceStream(type.Namespace + ".continueWatchingMenu.js");
        if (stream is null)
        {
            return null;
        }

        using StreamReader reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
