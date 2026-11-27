using System.Globalization;
using Dvc.Core.Compression;

namespace Dvc.Desktop.ViewModels;

/// <summary>
/// Sizes, durations and other numbers written out for the user. The app's
/// text is English, so numbers are formatted the same on every locale.
/// </summary>
public static class DisplayText
{
    /// <summary>How long an encode has to run before its time remaining is shown.</summary>
    public static readonly TimeSpan TimeRemainingDelay = TimeSpan.FromSeconds(5);

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly string[] SizeUnits = ["KB", "MB", "GB", "TB"];

    /// <summary>
    /// A file size such as "45.2 MB". Units are multiples of 1024, as in
    /// Windows Explorer, so the numbers match what it shows.
    /// </summary>
    public static string FileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        }

        double value = bytes;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < SizeUnits.Length - 1);

        return $"{value.ToString(value < 100 ? "0.0" : "0", Culture)} {SizeUnits[unit]}";
    }

    /// <summary>
    /// A duration to the nearest second, as "m:ss", or "h:mm:ss" from an
    /// hour up.
    /// </summary>
    public static string Duration(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Round(seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time:mm\\:ss}"
            : $"{time.Minutes}:{time:ss}";
    }

    /// <summary>
    /// What a probed video is, such as "1920×1080 · 0:42 · 29.97 fps ·
    /// 45.2 MB". The duration and framerate are left out when ffprobe
    /// couldn't tell.
    /// </summary>
    public static string SourceDetails(SourceInfo info, long sizeBytes)
    {
        var parts = new List<string> { $"{info.Width}×{info.Height}" };
        if (info.DurationSeconds > 0)
        {
            parts.Add(Duration(info.DurationSeconds));
        }

        if (info.Fps is { } fps)
        {
            parts.Add($"{fps.ToString("0.##", Culture)} fps");
        }

        parts.Add(FileSize(sizeBytes));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// How the compressed video's size compares with the original's, such as
    /// "45.2 MB → 12.1 MB, 73% smaller", or "… 20% larger than the original"
    /// when it grew.
    /// </summary>
    public static string SizeChange(long originalBytes, long compressedBytes)
    {
        var sizes = $"{FileSize(originalBytes)} → {FileSize(compressedBytes)}";
        if (originalBytes <= 0)
        {
            return sizes;
        }

        var change = (double)(compressedBytes - originalBytes) / originalBytes;
        var percent = (int)Math.Round(Math.Abs(change) * 100, MidpointRounding.AwayFromZero);

        if (percent == 0)
        {
            return $"{sizes}, about the same size as the original";
        }

        if (change > 0)
        {
            return $"{sizes}, {percent}% larger than the original";
        }

        // A file that kept any bytes at all didn't shrink by 100%.
        return $"{sizes}, {(compressedBytes > 0 ? Math.Min(percent, 99) : 100)}% smaller";
    }

    /// <summary>
    /// How long the rest of an encode should take, going by how long it took
    /// to get this far: <c>elapsed</c> to reach <c>fraction</c> (0 to 1).
    /// </summary>
    /// <returns>
    /// Null until the encode has run for <c>TimeRemainingDelay</c> and made
    /// some progress, since an estimate from the first moments jumps around.
    /// </returns>
    public static TimeSpan? EstimateTimeRemaining(double fraction, TimeSpan elapsed)
    {
        if (elapsed < TimeRemainingDelay || fraction <= 0.01)
        {
            return null;
        }

        return fraction >= 1 ? TimeSpan.Zero : elapsed * ((1 - fraction) / fraction);
    }

    /// <summary>
    /// A time remaining, rounded to suit its length: "About 45 s left",
    /// "About 3 min left" or "About 1 h 20 min left".
    /// </summary>
    public static string TimeRemaining(TimeSpan remaining)
    {
        if (remaining.TotalSeconds < 60)
        {
            // Rounded up to 5 seconds, so it never says 0.
            var seconds = Math.Max(5, (int)Math.Ceiling(remaining.TotalSeconds / 5) * 5);
            return seconds == 60 ? "About 1 min left" : $"About {seconds} s left";
        }

        var minutes = (int)Math.Round(remaining.TotalMinutes, MidpointRounding.AwayFromZero);
        return (minutes / 60, minutes % 60) switch
        {
            (0, var m) => $"About {m} min left",
            (var h, 0) => $"About {h} h left",
            (var h, var m) => $"About {h} h {m} min left",
        };
    }
}
