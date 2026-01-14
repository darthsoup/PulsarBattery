using Microsoft.UI.Xaml.Data;
using System;

namespace PulsarBattery.Converters;

public sealed class ChargingStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isCharging)
        {
            return isCharging ? "Charging" : "Not Charging";
        }

        return "Status: --";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
