using System.Globalization;
using System.Windows.Media;

namespace JarvisCode.App.Theming;

/// <summary>
/// Parses the two color formats theme tokens use: bare HSL triplets
/// ("15 63.1% 59.6%") and hex ("#d97757", "#d9775718" with trailing alpha).
/// </summary>
public static class CssColor
{
    public static bool TryParse(string value, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.StartsWith('#'))
        {
            return TryParseHex(value, out color);
        }

        if (value == "black")
        {
            color = Colors.Black;
            return true;
        }

        if (value == "white")
        {
            color = Colors.White;
            return true;
        }

        return TryParseHslTriplet(value, out color);
    }

    public static Color Parse(string value)
        => TryParse(value, out var color)
            ? color
            : throw new FormatException($"Unrecognized theme color '{value}'.");

    private static bool TryParseHex(string value, out Color color)
    {
        color = Colors.Transparent;
        var hex = value[1..];
        if (hex.Length is not (3 or 6 or 8))
        {
            return false;
        }

        if (hex.Length == 3)
        {
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
        }

        if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits))
        {
            return false;
        }

        // CSS hex order is RRGGBB[AA]; trailing byte is alpha.
        if (hex.Length == 8)
        {
            color = Color.FromArgb((byte)(bits & 0xFF), (byte)(bits >> 24), (byte)((bits >> 16) & 0xFF), (byte)((bits >> 8) & 0xFF));
        }
        else
        {
            color = Color.FromRgb((byte)(bits >> 16), (byte)((bits >> 8) & 0xFF), (byte)(bits & 0xFF));
        }

        return true;
    }

    private static bool TryParseHslTriplet(string value, out Color color)
    {
        color = Colors.Transparent;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ||
            !double.TryParse(parts[1].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ||
            !double.TryParse(parts[2].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var l))
        {
            return false;
        }

        color = FromHsl(h, s / 100.0, l / 100.0);
        return true;
    }

    public static Color FromHsl(double hue, double saturation, double lightness)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        lightness = Math.Clamp(lightness, 0, 1);

        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - c / 2;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));

        static byte ToByte(double channel) => (byte)Math.Round(Math.Clamp(channel, 0, 1) * 255);
    }

    public static Color WithAlpha(Color color, double alpha)
        => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);
}
