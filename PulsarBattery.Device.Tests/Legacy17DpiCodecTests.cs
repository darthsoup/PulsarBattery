using PulsarBattery.Device;

namespace PulsarBattery.Device.Tests;

public sealed class Legacy17DpiCodecTests
{
    public static TheoryData<int, int, string> GoldenStages => new()
    {
        { (int)Legacy17SensorKind.PulsarXs1, 400, "27270007" },
        { (int)Legacy17SensorKind.PulsarXs1, 3_200, "3F3F4493" },
        { (int)Legacy17SensorKind.PulsarXs1, 12_800, "373722C5" },
        { (int)Legacy17SensorKind.PulsarXs1, 32_000, "77773334" },
        { (int)Legacy17SensorKind.Paw3950, 50, "00000055" },
        { (int)Legacy17SensorKind.Paw3950, 32_000, "3F3F5582" },
        { (int)Legacy17SensorKind.Paw3955, 1, "000000000055" },
        { (int)Legacy17SensorKind.Paw3955, 400, "8F018F010035" },
        { (int)Legacy17SensorKind.Paw3955, 42_000, "0FA40FA400EF" },
    };

    [Theory]
    [MemberData(nameof(GoldenStages))]
    public void KnownStagesMatchCmouseEncoding(
        int sensorValue,
        int dpi,
        string expectedHex)
    {
        var sensor = (Legacy17SensorKind)sensorValue;
        var encoded = Legacy17DpiCodec.EncodeStage(sensor, dpi);

        Assert.NotNull(encoded);
        Assert.Equal(Convert.FromHexString(expectedHex), encoded);
        Assert.Equal(dpi, Legacy17DpiCodec.DecodeStage(sensor, encoded));
    }

    [Theory]
    [InlineData((int)Legacy17SensorKind.PulsarXs1)]
    [InlineData((int)Legacy17SensorKind.Paw3950)]
    [InlineData((int)Legacy17SensorKind.Paw3955)]
    public void EveryAdvertisedDpiValueRoundTrips(int sensorValue)
    {
        var sensor = (Legacy17SensorKind)sensorValue;
        foreach (var range in Legacy17DpiCodec.GetRanges(sensor))
        {
            for (var dpi = range.Minimum; dpi <= range.Maximum; dpi += range.Step)
            {
                var encoded = Legacy17DpiCodec.EncodeStage(sensor, dpi);
                Assert.True(encoded is not null, $"{sensor} failed to encode {dpi} DPI");
                Assert.Equal(dpi, Legacy17DpiCodec.DecodeStage(sensor, encoded!));
            }
        }
    }

    public static TheoryData<int, int> InvalidDpiValues => new()
    {
        { (int)Legacy17SensorKind.PulsarXs1, 0 },
        { (int)Legacy17SensorKind.PulsarXs1, 10_020 },
        { (int)Legacy17SensorKind.PulsarXs1, 10_060 },
        { (int)Legacy17SensorKind.PulsarXs1, 32_001 },
        { (int)Legacy17SensorKind.Paw3950, 1 },
        { (int)Legacy17SensorKind.Paw3950, 30_050 },
        { (int)Legacy17SensorKind.Paw3950, 32_001 },
        { (int)Legacy17SensorKind.Paw3955, 0 },
        { (int)Legacy17SensorKind.Paw3955, 42_001 },
    };

    [Theory]
    [MemberData(nameof(InvalidDpiValues))]
    public void ValuesOutsideSensorRangesAreRejected(int sensorValue, int dpi)
    {
        var sensor = (Legacy17SensorKind)sensorValue;
        Assert.Null(Legacy17DpiCodec.EncodeStage(sensor, dpi));
    }

    [Fact]
    public void DecodeRejectsCorruptInternalChecksum()
    {
        var encoded = Legacy17DpiCodec.EncodeStage(Legacy17SensorKind.PulsarXs1, 1_600)!;
        encoded[^1] ^= 0x01;

        Assert.Null(Legacy17DpiCodec.DecodeStage(Legacy17SensorKind.PulsarXs1, encoded));
    }

    [Theory]
    [InlineData((int)Legacy17SensorKind.PulsarXs1, 1)]
    [InlineData((int)Legacy17SensorKind.Paw3955, 2)]
    public void DecodeRejectsAsymmetricXAndYValues(int sensorValue, int yByteIndex)
    {
        var sensor = (Legacy17SensorKind)sensorValue;
        var encoded = Legacy17DpiCodec.EncodeStage(sensor, 400)!;
        encoded[yByteIndex] ^= 0x01;
        encoded[^1] = Legacy17Protocol.Checksum(encoded.AsSpan(0, encoded.Length - 1));

        Assert.Null(Legacy17DpiCodec.DecodeStage(sensor, encoded));
    }

    [Fact]
    public void StageAddressesUseStandardAndExtendedRegions()
    {
        Assert.Equal(0x000C, Legacy17DpiCodec.StageAddress(Legacy17SensorKind.PulsarXs1, 0));
        Assert.Equal(0x0020, Legacy17DpiCodec.StageAddress(Legacy17SensorKind.Paw3950, 5));
        Assert.Equal(0x1B00, Legacy17DpiCodec.StageAddress(Legacy17SensorKind.Paw3955, 0));
        Assert.Equal(0x1B1E, Legacy17DpiCodec.StageAddress(Legacy17SensorKind.Paw3955, 5));
        Assert.Equal(4, Legacy17DpiCodec.RecordLength(Legacy17SensorKind.PulsarXs1));
        Assert.Equal(6, Legacy17DpiCodec.RecordLength(Legacy17SensorKind.Paw3955));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Legacy17DpiCodec.StageAddress(Legacy17SensorKind.PulsarXs1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Legacy17DpiCodec.StageAddress(Legacy17SensorKind.Paw3955, 6));
    }
}
