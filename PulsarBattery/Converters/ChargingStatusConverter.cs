using Microsoft.UI.Xaml.Data;
using PulsarBattery.Tools;
using System;

namespace PulsarBattery.Converters;

public sealed class ChargingStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isCharging)
        {
            return isCharging ? Loc.T("Charging") : Loc.T("Not charging");
        }

        return Loc.T("Status: --");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
