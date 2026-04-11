using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Screenshot.Helpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Screenshot.Services;

/// <summary>
/// Startup task that registers the index.html injection with the File Transformation plugin.
/// Falls back to direct disk injection if File Transformation is not installed.
/// </summary>
public class StartupService : IScheduledTask
{
    private readonly ILogger<StartupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupService"/> class.
    /// </summary>
    public StartupService(ILogger<StartupService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Screenshot Capture Startup";

    /// <inheritdoc />
    public string Key => "ScreenshotCaptureStartup";

    /// <inheritdoc />
    public string Description => "Registers the screenshot capture script injection via the File Transformation plugin.";

    /// <inheritdoc />
    public string Category => "Screenshot Capture";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        RegisterFileTransformation();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };
    }

    private void RegisterFileTransformation()
    {
        Assembly? ftAssembly = AssemblyLoadContext.All
            .SelectMany(ctx => ctx.Assemblies)
            .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation") ?? false);

        if (ftAssembly is null)
        {
            _logger.LogInformation("File Transformation plugin not found — using direct index.html injection fallback.");
            Plugin.Instance?.InjectScript();
            return;
        }

        var pluginInterfaceType = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");

        if (pluginInterfaceType is null)
        {
            _logger.LogWarning("FileTransformation assembly found but PluginInterface type is missing — using fallback.");
            Plugin.Instance?.InjectScript();
            return;
        }

        var payload = new JObject
        {
            { "id", Plugin.Instance?.Id.ToString() ?? Guid.NewGuid().ToString() },
            { "fileNamePattern", "index.html" },
            { "callbackAssembly", typeof(TransformationPatches).Assembly.FullName },
            { "callbackClass", typeof(TransformationPatches).FullName },
            { "callbackMethod", nameof(TransformationPatches.IndexHtml) }
        };

        pluginInterfaceType.GetMethod("RegisterTransformation")?.Invoke(null, new object[] { payload });

        _logger.LogInformation("Screenshot Capture script injection registered with File Transformation plugin.");
    }
}
