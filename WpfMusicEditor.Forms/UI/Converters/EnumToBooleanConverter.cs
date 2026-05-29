using System.Globalization;
using System.Windows.Data;

namespace WpfMusicEditor.Forms.UI.Converters;

/// <summary>
/// 라디오 버튼류를 enum 값에 묶을 때 쓴다. ConverterParameter 로 받은 값과 같으면 true.
/// </summary>
public class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.Equals(parameter) ?? false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is not null ? parameter : Binding.DoNothing;
}
