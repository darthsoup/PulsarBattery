using System;
using System.Collections.Generic;

namespace PulsarBattery.Device;

/// <summary>
/// Encodes the DPI records used by Pulsar's cMouse 17-byte protocol. The
/// ranges and exponent codes are the three layouts shipped in cMouse V1.31.
/// </summary>
internal static class Legacy17DpiCodec
{
    private const ushort StandardDpiAddress = 0x000C;
    private const ushort Extended3955DpiAddress = 0x1B00;

    private static readonly IReadOnlyList<DeviceValueRange> PulsarXs1Ranges =
    [
        new(10, 10_000, 10),
        new(10_050, 30_000, 50),
        new(30_100, 32_000, 100),
    ];

    private static readonly IReadOnlyList<DeviceValueRange> Paw3950Ranges =
    [
        new(50, 30_000, 50),
        new(30_100, 32_000, 100),
    ];

    private static readonly IReadOnlyList<DeviceValueRange> Paw3955Ranges =
    [
        new(1, 42_000, 1),
    ];

    public static IReadOnlyList<DeviceValueRange> GetRanges(Legacy17SensorKind sensor) => sensor switch
    {
        Legacy17SensorKind.PulsarXs1 => PulsarXs1Ranges,
        Legacy17SensorKind.Paw3950 => Paw3950Ranges,
        Legacy17SensorKind.Paw3955 => Paw3955Ranges,
        _ => Array.Empty<DeviceValueRange>(),
    };

    public static int RecordLength(Legacy17SensorKind sensor) =>
        sensor == Legacy17SensorKind.Paw3955 ? 6 : 4;

    public static ushort StageAddress(Legacy17SensorKind sensor, int zeroBasedStage)
    {
        if (zeroBasedStage is < 0 or >= 6)
        {
            throw new ArgumentOutOfRangeException(nameof(zeroBasedStage));
        }

        return sensor == Legacy17SensorKind.Paw3955
            ? (ushort)(Extended3955DpiAddress + (zeroBasedStage * 6))
            : (ushort)(StandardDpiAddress + (zeroBasedStage * 4));
    }

    public static byte[]? EncodeStage(Legacy17SensorKind sensor, int dpi)
    {
        if (!TryEncodeScalar(sensor, dpi, out var raw, out var exponent))
        {
            return null;
        }

        if (sensor == Legacy17SensorKind.Paw3955)
        {
            var block = new byte[6];
            block[0] = (byte)raw;
            block[1] = (byte)(raw >> 8);
            block[2] = (byte)raw;
            block[3] = (byte)(raw >> 8);
            block[4] = PackAttributes(raw, exponent, raw, exponent, highShift: 16);
            block[5] = Legacy17Protocol.Checksum(block.AsSpan(0, 5));
            return block;
        }

        var standard = new byte[4];
        standard[0] = (byte)raw;
        standard[1] = (byte)raw;
        standard[2] = PackAttributes(raw, exponent, raw, exponent, highShift: 8);
        standard[3] = Legacy17Protocol.Checksum(standard.AsSpan(0, 3));
        return standard;
    }

    public static int? DecodeStage(Legacy17SensorKind sensor, IReadOnlyList<byte> block)
    {
        var length = RecordLength(sensor);
        if (block.Count < length || Legacy17Protocol.Checksum(ToArray(block, length - 1)) != block[length - 1])
        {
            return null;
        }

        var attributes = block[sensor == Legacy17SensorKind.Paw3955 ? 4 : 2];
        var xExponent = (byte)(attributes & 0b11);
        var yExponent = (byte)((attributes >> 4) & 0b11);
        var xHigh = (attributes >> 2) & 0b11;
        var yHigh = (attributes >> 6) & 0b11;
        var xRaw = sensor == Legacy17SensorKind.Paw3955
            ? block[0] | (block[1] << 8) | (xHigh << 16)
            : block[0] | (xHigh << 8);
        var yRaw = sensor == Legacy17SensorKind.Paw3955
            ? block[2] | (block[3] << 8) | (yHigh << 16)
            : block[1] | (yHigh << 8);

        // DeviceSettings currently exposes one DPI value. Preserve an
        // asymmetric X/Y profile as unknown instead of displaying X and then
        // silently overwriting Y when the user edits it.
        if (xRaw != yRaw || xExponent != yExponent)
        {
            return null;
        }

        return DecodeScalar(sensor, xRaw, xExponent);
    }

    private static bool TryEncodeScalar(
        Legacy17SensorKind sensor,
        int dpi,
        out int raw,
        out byte exponent)
    {
        raw = 0;
        exponent = 0;

        var ranges = GetRanges(sensor);
        var rangeIndex = -1;
        for (var i = 0; i < ranges.Count; i++)
        {
            if (ranges[i].Contains(dpi))
            {
                rangeIndex = i;
                break;
            }
        }

        if (rangeIndex < 0)
        {
            return false;
        }

        exponent = sensor switch
        {
            Legacy17SensorKind.PulsarXs1 => rangeIndex switch { 0 => 0, 1 => 2, _ => 3 },
            Legacy17SensorKind.Paw3950 => rangeIndex == 0 ? (byte)0 : (byte)1,
            Legacy17SensorKind.Paw3955 => 0,
            _ => 0,
        };

        var value = dpi;
        if ((exponent & 0b01) != 0)
        {
            value /= 2;
        }

        if ((exponent & 0b10) != 0)
        {
            value = sensor == Legacy17SensorKind.PulsarXs1
                ? (value - 10_000) / 5
                : value / 2;
        }

        var baseStep = ranges[0].Step;
        raw = (value / baseStep) - 1;
        return raw >= 0;
    }

    private static int? DecodeScalar(Legacy17SensorKind sensor, int raw, byte exponent)
    {
        var ranges = GetRanges(sensor);
        if (ranges.Count == 0)
        {
            return null;
        }

        var allowedExponent = sensor switch
        {
            Legacy17SensorKind.PulsarXs1 => exponent is 0 or 2 or 3,
            Legacy17SensorKind.Paw3950 => exponent is 0 or 1,
            Legacy17SensorKind.Paw3955 => exponent == 0,
            _ => false,
        };
        if (!allowedExponent)
        {
            return null;
        }

        var dpi = (raw + 1) * ranges[0].Step;
        if ((exponent & 0b10) != 0)
        {
            dpi = sensor == Legacy17SensorKind.PulsarXs1
                ? (dpi * 5) + 10_000
                : dpi * 2;
        }

        if ((exponent & 0b01) != 0)
        {
            dpi *= 2;
        }

        foreach (var range in ranges)
        {
            if (range.Contains(dpi))
            {
                return dpi;
            }
        }

        return null;
    }

    private static byte PackAttributes(
        int xRaw,
        byte xExponent,
        int yRaw,
        byte yExponent,
        int highShift)
    {
        var xHigh = (byte)((xRaw >> highShift) & 0b11);
        var yHigh = (byte)((yRaw >> highShift) & 0b11);
        return (byte)((xHigh << 2) | (yHigh << 6) | xExponent | (yExponent << 4));
    }

    private static byte[] ToArray(IReadOnlyList<byte> source, int count)
    {
        var result = new byte[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = source[i];
        }

        return result;
    }
}
