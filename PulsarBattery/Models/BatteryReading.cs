using PulsarBattery.Tools;
using System;
using System.Globalization;
using System.Text.Json.Serialization;

namespace PulsarBattery.Models;

public sealed record BatteryReading(DateTimeOffset Timestamp, int Percentage, bool IsCharging, string Model)
{
    [JsonIgnore]
    public string FormattedTimestamp => Timestamp.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
    
    [JsonIgnore]
    public string ChargingStatus => IsCharging ? Loc.T("Yes") : Loc.T("No");
}
