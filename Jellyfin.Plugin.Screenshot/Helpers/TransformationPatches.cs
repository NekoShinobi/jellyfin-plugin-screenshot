using System.Text.RegularExpressions;
using Jellyfin.Plugin.Screenshot.Model;

namespace Jellyfin.Plugin.Screenshot.Helpers;

/// <summary>
/// Static transformation callbacks invoked by the File Transformation plugin
/// to modify web files in-memory without touching the disk.
/// </summary>
public static class TransformationPatches
{
    /// <summary>
    /// Injects the Screenshot Capture script tag into index.html.
    /// Called by the File Transformation plugin when index.html is served.
    /// </summary>
    /// <param name="content">The raw index.html content from the File Transformation plugin.</param>
    /// <returns>The transformed HTML with the script tag injected before &lt;/body&gt;.</returns>
    public static string IndexHtml(PatchRequestPayload content)
    {
        if (string.IsNullOrEmpty(content.Contents))
        {
            return content.Contents ?? string.Empty;
        }

        var pluginName = Plugin.Instance?.Name ?? "Screenshot Capture";
        var pluginVersion = Plugin.Instance?.Version.ToString() ?? "1.0.0.0";

        var scriptUrl = $"../Screenshot/script?v={pluginVersion}";
        var scriptTag = $"<script plugin=\"{pluginName}\" version=\"{pluginVersion}\" src=\"{scriptUrl}\" defer></script>";

        // Remove any stale version of our tag first
        var regex = new Regex($"<script[^>]*plugin=[\"']{Regex.Escape(pluginName)}[\"'][^>]*>\\s*</script>\\n?");
        var updated = regex.Replace(content.Contents, string.Empty);

        // Inject before closing body tag
        if (updated.Contains("</body>"))
        {
            return updated.Replace("</body>", $"{scriptTag}\n</body>");
        }

        return updated;
    }
}
