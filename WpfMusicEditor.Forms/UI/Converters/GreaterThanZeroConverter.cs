using System.Globalization;
using System.Windows.Data;

namespace WpfMusicEditor.Forms.UI.Converters;

/// <summary>숫자가 0보다 크면 true. (확대됐을 때만 스크롤 슬라이더를 활성화하는 용도)</summary>
public class GreaterThanZeroConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d && d > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
