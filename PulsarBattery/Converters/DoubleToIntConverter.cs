using Microsoft.UI.Xaml.Data;
using System;

namespace PulsarBattery.Converters;

public sealed class DoubleToIntConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is int i ? (double)i : 0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is double d)
        {
            return (int)Math.Round(d);
        }

        return 0;
    }
}

