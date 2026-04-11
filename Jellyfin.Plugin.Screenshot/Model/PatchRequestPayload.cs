using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Screenshot.Model;

/// <summary>
/// Payload sent by the File Transformation plugin when requesting an index.html transform.
/// </summary>
public class PatchRequestPayload
{
    /// <summary>
    /// Gets or sets the raw file contents to be transformed.
    /// </summary>
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}
