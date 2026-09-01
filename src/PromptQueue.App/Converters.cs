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
