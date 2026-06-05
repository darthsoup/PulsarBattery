using Microsoft.UI.Xaml.Data;
using PulsarBattery.Tools;
using System;

namespace PulsarBattery.Converters;

public sealed class BatteryPercentageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int percentage)
        {
            return string.Format(Loc.T("Battery: {0}%"), percentage);
        }

        return Loc.T("Battery: --");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
