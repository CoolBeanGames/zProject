using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PromptQueue.App;

/// <summary>
/// Visible when every bound value is empty/false, otherwise collapsed. Used for
/// the "nothing recorded yet" placeholder line in the task tooltip. Pass
/// "invert" to flip (visible when any value is populated).
/// </summary>
public sealed class AllEmptyToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool anyPopulated = values.Any(v => v switch
        {
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            null => false,
            _ => true,
        });
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
            return anyPopulated ? Visibility.Visible : Visibility.Collapsed;
        return anyPopulated ? Visibility.Collapsed : Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True → strikethrough decorations, used to cross out completed tasks.</summary>
public sealed class BoolToStrikethroughConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? TextDecorations.Strikethrough : null!;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>True → visible, false → collapsed. Optional "invert" parameter.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value switch
        {
            bool b => b,
            int i => i != 0,
            null => false,
            string s => !string.IsNullOrEmpty(s),
            _ => true,
        };
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Non-empty string → visible, else collapsed.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Maps a tag string to a stable, pale background colour so the same tag is
/// always the same colour (ZP-26). "Random" but deterministic — no storage.
/// </summary>
public sealed class TagColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var tag = (value as string ?? "").Trim().ToLowerInvariant();
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in tag)
                h = (h ^ c) * 16777619;
            var hue = h % 360;
            var (r, g, b) = HslToRgb(hue, 0.45, 0.90);   // pale, low-ish saturation
            var (dr, dg, db) = HslToRgb(hue, 0.55, 0.32); // matching darker text
            if (string.Equals(parameter as string, "text", StringComparison.OrdinalIgnoreCase))
                return new SolidColorBrush(Color.FromRgb(dr, dg, db));
            if (string.Equals(parameter as string, "border", StringComparison.OrdinalIgnoreCase))
            {
                var (br, bg, bb) = HslToRgb(hue, 0.40, 0.72);
                return new SolidColorBrush(Color.FromRgb(br, bg, bb));
            }
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static (byte, byte, byte) HslToRgb(double h, double s, double l)
    {
        h /= 360.0;
        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3);
            g = Hue(p, q, h);
            b = Hue(p, q, h - 1.0 / 3);
        }
        return ((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }
}

/// <summary>Collapsed → ChevronRight, expanded → ChevronDown (Segoe MDL2 Assets).</summary>
public sealed class ChevronGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Splits "a, b, c" into a list for an ItemsControl of tag chips.</summary>
public sealed class TagListConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => PromptQueue.Core.Models.TaskItem.SplitTags(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>true → closed padlock, false → open padlock (Segoe MDL2 Assets glyphs).</summary>
public sealed class LockGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>true → "☑", false → "☐" (subtask state in tooltips).</summary>
public sealed class CheckGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "☑" : "☐";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>true → "Show", false → "Hide" (for the Completed-section collapse toggle).</summary>
public sealed class BoolToShowHideConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "Show" : "Hide";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Collapses whitespace/newlines to single spaces for one-line previews.</summary>
public sealed class SingleLinePreviewConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string ?? "";
        var collapsed = string.Join(' ', text.Split(
            new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
