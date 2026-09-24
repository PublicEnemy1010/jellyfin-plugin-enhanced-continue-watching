using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.ContinueWatching.Web;

internal static class FileTransformationRegistrar
{
    private const string TransformationId = "a1f3c9de-2b4e-4f7a-9c1d-6e8b0f5a7d3c";
    private const string FileTransformationInterfaceTypeName = "Jellyfin.Plugin.FileTransformation.PluginInterface";

    public static bool TryRegister(ILogger logger)
    {
        try
        {
            Assembly? fileTransformationAssembly = AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(assembly => assembly.FullName?.Contains(".FileTransformation", System.StringComparison.OrdinalIgnoreCase) ?? false);

            if (fileTransformationAssembly is null)
            {
                logger.LogInformation("File Transformation plugin was not found; the Continue Watching menu button will not be injected");
                return false;
            }

            Type? pluginInterfaceType = fileTransformationAssembly.GetType(FileTransformationInterfaceTypeName);
            if (pluginInterfaceType is null)
            {
                logger.LogWarning("File Transformation plugin interface was not found");
                return false;
            }

            MethodInfo? registerTransformationMethod = pluginInterfaceType.GetMethod("RegisterTransformation");
            if (registerTransformationMethod is null)
            {
                logger.LogWarning("RegisterTransformation method was not found on the File Transformation plugin interface");
                return false;
            }

            JObject payload = new JObject
            {
                { "id", TransformationId },
                { "fileNamePattern", "index.html" },
                { "callbackAssembly", typeof(FileTransformationRegistrar).Assembly.FullName },
                { "callbackClass", typeof(ContinueWatchingMenuTransformation).FullName },
                { "callbackMethod", nameof(ContinueWatchingMenuTransformation.IndexTransformation) }
            };

            registerTransformationMethod.Invoke(null, new object?[] { payload });
            logger.LogInformation("Continue Watching successfully registered its File Transformation patch");
            return true;
        }
        catch (System.Exception ex)
        {
            logger.LogError(ex, "Failed to register Continue Watching with the File Transformation plugin");
            return false;
        }
    }
}
