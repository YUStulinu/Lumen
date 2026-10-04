using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lumen.App.Controls;

/// <summary>
/// The "working…" bar is visible only while an answer is streaming and nothing has arrived yet:
/// once the first words appear, they are the progress indicator.
/// </summary>
public static class StreamingIndicator
{
    public static IMultiValueConverter Converter { get; } = new VisibilityConverter();

    private sealed class VisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values is [true, string text] && text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
