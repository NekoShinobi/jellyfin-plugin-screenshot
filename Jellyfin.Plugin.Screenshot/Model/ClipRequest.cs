using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.Screenshot.Model;

#pragma warning disable CS1591
/// <summary>A bounded selection around a frozen playback timestamp.</summary>
public sealed class ClipRequest
{
    public Guid ItemId { get; set; }
    public string? MediaSourceId { get; set; }
    public long AnchorTicks { get; set; }
    // Absolute endpoints support selections wholly before or after the anchor.
    public long? StartTicks { get; set; }
    public long? EndTicks { get; set; }
    // Retain the original fields for clients loaded before a server upgrade.
    [Range(0, 600000000)]
    public long BeforeTicks { get; set; }
    [Range(0, 600000000)]
    public long AfterTicks { get; set; }
    public int? AudioStreamIndex { get; set; }
    public int? SubtitleStreamIndex { get; set; }
    public bool Preview { get; set; }
    [RegularExpression("^(mp4|webm)$")]
    public string PreviewFormat { get; set; } = "mp4";
}

internal readonly record struct ClipRange(long StartTicks, long EndTicks)
{
    public long DurationTicks => EndTicks - StartTicks;

    public static ClipRange FromRequest(ClipRequest request, long runtime)
    {
        if (request.StartTicks is null && request.EndTicks is null)
            return Create(request.AnchorTicks, request.BeforeTicks, request.AfterTicks, runtime);
        if (request.StartTicks is not long start || request.EndTicks is not long end)
            throw new ArgumentException("Specify both the starting point and end of the clip.");

        var window = Create(request.AnchorTicks, 60 * TimeSpan.TicksPerSecond, 60 * TimeSpan.TicksPerSecond, runtime);
        if (start < window.StartTicks || end > window.EndTicks || end <= start)
            throw new ArgumentException("Select a non-empty clip within the fixed window around the requested moment.");
        return new ClipRange(start, end);
    }

    public static ClipRange Create(long anchor, long before, long after, long runtime)
    {
        const long limit = 60 * TimeSpan.TicksPerSecond;
        if (runtime <= 0 || anchor < 0 || anchor > runtime
            || before < 0 || before > limit || after < 0 || after > limit)
        {
            throw new ArgumentException("Choose up to 60 seconds before and after a position within the video.");
        }

        // Subtract first so even hostile Int64 values cannot overflow an addition.
        var range = new ClipRange(anchor - Math.Min(before, anchor), anchor + Math.Min(after, runtime - anchor));
        if (range.DurationTicks <= 0)
        {
            throw new ArgumentException("Select a clip longer than zero seconds.");
        }

        return range;
    }
}
