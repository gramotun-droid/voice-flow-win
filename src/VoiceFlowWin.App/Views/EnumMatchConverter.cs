using System.Globalization;
using System.Windows.Data;

namespace VoiceFlowWin.App.Views;

/// <summary>
/// Связывает RadioButton со значением перечисления.
/// </summary>
/// <remarks>
/// Кнопка отмечена, когда значение свойства совпадает с ConverterParameter,
/// и записывает это значение обратно при выборе. Так настройки вроде режима
/// активации или поведения Esc остаются обычными enum'ами, без дублирующих
/// булевых свойств на каждый вариант.
/// </remarks>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
        {
            // Снятие отметки обрабатывает та кнопка, которую выбрали.
            return Binding.DoNothing;
        }

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return Enum.Parse(enumType, parameter.ToString()!);
    }
}
