using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Screenshot.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Screenshot.Tests;

public sealed class LocaleTests
{
    private static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.Compiled);

    public static TheoryData<string> Translations()
    {
        var data = new TheoryData<string>();
        foreach (var code in ScriptBundle.ReadLocales().Keys.Where(code => code != "en")) data.Add(code);
        return data;
    }

    [Fact]
    public void BundleEmbedsEnglishAndSeveralTranslations()
    {
        var locales = ScriptBundle.ReadLocales();
        Assert.Contains("en", locales.Keys);
        Assert.True(locales.Count > 5, string.Join(", ", locales.Keys));

        var script = ScriptBundle.Build();
        Assert.NotNull(script);
        // Translations must be defined before the runtime reads them.
        Assert.StartsWith("window.ScreenshotCaptureLocales = {", script);
        Assert.True(script.IndexOf("ScreenshotCaptureI18n = {", StringComparison.Ordinal)
            < script.IndexOf("ScreenshotCaptureTools = {", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void TranslationsMatchEnglishKeysAndPlaceholders(string code)
    {
        var locales = ScriptBundle.ReadLocales();
        var english = Strings(locales["en"]);
        var translation = Strings(locales[code]);

        Assert.Empty(english.Keys.Except(translation.Keys));
        Assert.Empty(translation.Keys.Except(english.Keys));
        foreach (var (key, text) in translation)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), $"{code}: {key} is empty");
            Assert.Equal(Placeholders(english[key]), Placeholders(text));
        }
    }

    private static Dictionary<string, string> Strings(string json)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

    private static string[] Placeholders(string text)
        => Placeholder.Matches(text).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
}
