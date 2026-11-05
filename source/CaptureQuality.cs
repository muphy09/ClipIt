namespace ClipIt;

internal static class CaptureQuality
{
    internal static int[] Resolutions(int monitorHeight)
    {
        int maximum = Math.Max(2, monitorHeight / 2 * 2);
        return new[] { 720, 1080, 1440, 2160, maximum }.Where(h => h <= maximum).Distinct().Order().ToArray();
    }

    // Local H.264 quality targets, scaled by pixel count; 1080p60 uses 24 Mbps.
    internal static int RecommendedBitrate(int height, int fps) =>
        Math.Clamp((int)Math.Round(24 * Math.Pow(height / 1080.0, 2) * (fps == 30 ? 2.0 / 3 : 1)), 5, 100) * 1000;
}
