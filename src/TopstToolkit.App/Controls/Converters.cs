using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TopstToolkit.App.Controls;

/// <summary>true → Visible, false → Collapsed. <c>ConverterParameter=invert</c> flips it. Null and empty text count as false.</summary>
public sealed class VisibleWhen : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            null => false,
            _ => true,
        };
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
            on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>true ↔ false, for <c>IsEnabled="{Binding IsBusy, Converter={StaticResource Not}}"</c>.</summary>
public sealed class Not : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Files dragged onto a page from File Explorer.</summary>
public static class DropFiles
{
    /// <summary>Shows the copy cursor when files are dragged over; anything else is refused.</summary>
    public static void Accept(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    public static IReadOnlyList<string> All(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files ? files : [];

    public static string? First(DragEventArgs e) => All(e).FirstOrDefault();
}
