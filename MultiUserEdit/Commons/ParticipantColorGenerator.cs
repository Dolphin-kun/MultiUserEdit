using System.Windows.Media;

namespace MultiUserEdit.Commons
{
    internal static class ParticipantColorGenerator
    {
        public static Color Generate(Guid userId)
        {
            var bytes = userId.ToByteArray();
            int seed = BitConverter.ToInt32(bytes, 0);
            double goldenRatio = 0.618033988749895;
            double hue = (Math.Abs(seed) * goldenRatio) % 1.0;
            return HslToRgb(hue, 0.85, 0.55);
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            double r, g, b;
            if (s == 0)
            {
                r = g = b = l;
            }
            else
            {
                static double hue2rgb(double p, double q, double t)
                {
                    if (t < 0) t += 1;
                    if (t > 1) t -= 1;
                    if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                    if (t < 1.0 / 2) return q;
                    if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                    return p;
                }

                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = hue2rgb(p, q, h + 1.0 / 3);
                g = hue2rgb(p, q, h);
                b = hue2rgb(p, q, h - 1.0 / 3);
            }
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }
}
