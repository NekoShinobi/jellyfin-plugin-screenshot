using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Screenshot.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Screenshot;

/// <summary>
/// Screenshot Capture plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<Plugin> _logger;
    private const string PluginName = "Screenshot Capture";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationPaths = applicationPaths;
        _logger = logger;
        // Remove any stale fallback-injected script tags left from a previous run
        // that didn't have the File Transformation plugin available.
        CleanupOldScript();
    }

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a3c9f1d2-4b7e-4f8a-9d0c-1e2b3a4c5d6e");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    private string IndexHtmlPath => Path.Combine(_applicationPaths.WebPath, "index.html");

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        RemoveScript();
        base.OnUninstalling();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = this.Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        ];
    }

    /// <summary>
    /// Directly injects the script tag into index.html on disk.
    /// Used as a fallback when the File Transformation plugin is not installed.
    /// </summary>
    public void InjectScript()
    {
        try
        {
            var indexPath = IndexHtmlPath;
            if (!File.Exists(indexPath))
            {
                _logger.LogError("Could not find index.html at: {Path}", indexPath);
                return;
            }

            var content = File.ReadAllText(indexPath);
            var scriptUrl = $"../Screenshot/script?v={Version}";
            var scriptTag = $"<script plugin=\"{Name}\" version=\"{Version}\" src=\"{scriptUrl}\" defer></script>";
            var closingBodyTag = "</body>";

            if (content.Contains(scriptTag))
            {
                return; // Already injected this exact version
            }

            if (content.Contains(closingBodyTag))
            {
                content = content.Replace(closingBodyTag, $"{scriptTag}\n{closingBodyTag}");
                File.WriteAllText(indexPath, content);
                _logger.LogInformation("{Plugin} script injected into index.html", PluginName);
            }
            else
            {
                _logger.LogWarning("Could not find </body> in index.html — script not injected");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error injecting script into index.html");
        }
    }

    private void CleanupOldScript()
    {
        try
        {
            var indexPath = IndexHtmlPath;
            if (!File.Exists(indexPath))
            {
                return;
            }

            var content = File.ReadAllText(indexPath);
            var regex = new Regex($"<script[^>]*plugin=[\"']{Regex.Escape(Name)}[\"'][^>]*>\\s*</script>\\n?");

            if (regex.IsMatch(content))
            {
                content = regex.Replace(content, string.Empty);
                File.WriteAllText(indexPath, content);
                _logger.LogInformation("Removed old {Plugin} script tag from index.html", PluginName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old script from index.html");
        }
    }

    private void RemoveScript()
    {
        try
        {
            var indexPath = IndexHtmlPath;
            if (!File.Exists(indexPath))
            {
                return;
            }

            var content = File.ReadAllText(indexPath);
            var regex = new Regex($"<script[^>]*plugin=[\"']{Regex.Escape(Name)}[\"'][^>]*>\\s*</script>\\n?");
            content = regex.Replace(content, string.Empty);
            File.WriteAllText(indexPath, content);
            _logger.LogInformation("{Plugin} script removed from index.html on uninstall", PluginName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing script from index.html");
        }
    }
}
