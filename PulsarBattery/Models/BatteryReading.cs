using System;
using System.Globalization;

namespace PulsarBattery.Models;

public sealed record BatteryReading(DateTimeOffset Timestamp, int Percentage, bool IsCharging, string Model)
{
    public string FormattedTimestamp => Timestamp.ToString("G", CultureInfo.CurrentCulture);
    
    public string ChargingStatus => IsCharging ? "Yes" : "No";
}