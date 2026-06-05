using PulsarBattery.Models;
using PulsarBattery.Services;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PulsarBattery.Tools;

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class SettingsJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(List<BatteryReading>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CompactJsonContext : JsonSerializerContext { }
