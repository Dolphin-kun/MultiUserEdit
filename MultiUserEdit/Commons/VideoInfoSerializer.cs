using System.Windows.Media;
using YukkuriMovieMaker.Project;

namespace MultiUserEdit.Commons
{
    internal static class VideoInfoSerializer
    {
        public static string ToText(Color color) =>
            $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

        public static Color? ToColor(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            try
            {
                if (ColorConverter.ConvertFromString(text) is Color color) return color;
            }
            catch { }

            return null;
        }

        public static void Apply(VideoInfo target, int width, int height, int fps, int hz, string? backgroundColor)
        {
            if (width > 0) target.Width = width;
            if (height > 0) target.Height = height;
            if (fps > 0) target.FPS = fps;
            if (hz > 0) target.Hz = hz;
            if (ToColor(backgroundColor) is { } color) target.BackgroundColor = color;
        }
    }
}
