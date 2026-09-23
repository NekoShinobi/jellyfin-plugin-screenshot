using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Screenshot.Helpers;

/// <summary>
/// Builds the player script from the embedded translations and scripts.
/// </summary>
internal static class ScriptBundle
{
    private static readonly string Prefix = typeof(Plugin).Namespace + ".js.";
    private static readonly string LocalePrefix = Prefix + "locales.";

    /// <summary>
    /// Scripts in load order. The translation runtime must run before the scripts that use it.
    /// </summary>
    private static readonly string[] Scripts = ["i18n.js", "screenshot.js", "clipping.js"];

    /// <summary>
    /// Gets the embedded translations, keyed by locale code (for example <c>pt-BR</c>).
    /// </summary>
    internal static SortedDictionary<string, string> ReadLocales()
    {
        var assembly = typeof(ScriptBundle).Assembly;
        var locales = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(LocalePrefix, StringComparison.Ordinal)
                || !name.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            var code = name[LocalePrefix.Length..^".json".Length];
            locales[code] = Read(assembly, name)!;
        }

        return locales;
    }

    /// <summary>
    /// Returns the complete player script, or <c>null</c> if a script resource is missing.
    /// </summary>
    internal static string? Build()
    {
        var assembly = typeof(ScriptBundle).Assembly;
        var builder = new StringBuilder("window.ScreenshotCaptureLocales = {");
        var first = true;
        foreach (var (code, json) in ReadLocales())
        {
            // A malformed translation must not break the player; the runtime falls back to English.
            try
            {
                using var _ = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                continue;
            }

            builder.Append(first ? "\n" : ",\n").Append(JsonSerializer.Serialize(code)).Append(": ").Append(json.Trim());
            first = false;
        }

        builder.Append("\n};");
        var parts = new List<string> { builder.ToString() };
        foreach (var script in Scripts)
        {
            var content = Read(assembly, Prefix + script);
            if (content is null) return null;
            parts.Add(content);
        }

        return string.Join("\n;\n", parts);
    }

    private static string? Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
