using Microsoft.UI.Xaml.Data;
using System;

namespace PulsarBattery.Converters;

public sealed class BatteryTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int percentage)
        {
            return $"Pulsar Battery - {percentage}%";
        }

        return "Pulsar Battery";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
