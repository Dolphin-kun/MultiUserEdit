namespace MultiUserEdit.Commons
{
    internal static class DurationFormatter
    {
        public static string Format(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}.{duration.Minutes:00}.{duration.Seconds:00}"
                : $"{duration.Minutes:00}.{duration.Seconds:00}";
        }

        public static string FormatSeconds(double seconds) => Format(TimeSpan.FromSeconds(seconds));
    }
}
