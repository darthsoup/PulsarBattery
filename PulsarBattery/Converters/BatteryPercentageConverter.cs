using Microsoft.UI.Xaml.Data;
using System;

namespace PulsarBattery.Converters;

public sealed class BatteryPercentageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int percentage)
        {
            return $"Battery: {percentage}%";
        }

        return "Battery: --";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
