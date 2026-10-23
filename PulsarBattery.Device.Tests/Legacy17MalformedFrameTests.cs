using PulsarBattery.Device;

namespace PulsarBattery.Device.Tests;

public sealed class Legacy17MalformedFrameTests
{
    [Theory]
    [InlineData(0x08)]
    [InlineData(0x09)]
    public void BatteryAcceptsCompleteChecksummedReportsWithEitherInputId(byte reportId)
    {
        var response = BatteryFrame(reportId, 73, 1, 0x10, 0x04);

        var parsed = Legacy17Protocol.ParseBatteryPayload(response);

        Assert.NotNull(parsed);
        Assert.Equal((73, true, (int?)4100), parsed.Value);
    }

    [Fact]
    public void BatteryRejectsEveryTruncatedFrameAndEverySingleByteCorruption()
    {
        var response = BatteryFrame(0x08, 73, 1, 0x10, 0x04);
        for (var length = 0; length < response.Length; length++)
        {
            Assert.Null(Legacy17Protocol.ParseBatteryPayload(response[..length]));
        }

        for (var index = 0; index < response.Length; index++)
        {
            var corrupt = response.ToArray();
            corrupt[index] ^= 0x01;
            Assert.Null(Legacy17Protocol.ParseBatteryPayload(corrupt));
        }
    }

    [Theory]
    [InlineData(1, Legacy17Protocol.CmdVersion)]
    [InlineData(2, 1)]
    public void BatteryRejectsWrongCommandAndStatusOnlyAcknowledgement(int index, byte value)
    {
        var response = BatteryFrame(0x08, 73, 1, 0x10, 0x04);
        response[index] = value;
        RecalculateChecksum(response);

        Assert.Null(Legacy17Protocol.ParseBatteryPayload(response));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 1, true)]
    [InlineData(100, 0, true)]
    [InlineData(101, 0, false)]
    public void BatteryPreservesSleepAndChargingBoundarySemantics(byte percentage, byte charging, bool valid)
    {
        var response = BatteryFrame(0x08, percentage, charging, 0, 0);
        var parsed = Legacy17Protocol.ParseBatteryPayload(response);

        Assert.Equal(valid, parsed.HasValue);
        if (parsed.HasValue)
        {
            Assert.Null(parsed.Value.voltageMv);
        }
    }

    [Theory]
    [InlineData(0x08)]
    [InlineData(0x09)]
    public void BareReportsRecoverTheirOriginalReportIdFromChecksum(byte originalReportId)
    {
        var response = BatteryFrame(originalReportId, 73, 0, 0, 0);

        var normalized = Legacy17Protocol.Normalize(
            response[1..], 0x09, new HashSet<byte> { 0x08, 0x09 }, static command => command == Legacy17Protocol.CmdBattery);

        Assert.Equal(response, normalized);
        Assert.True(Legacy17Protocol.HasValidChecksum(normalized));
    }

    [Fact]
    public void NormalizationDoesNotRewriteAnExplicitReportIdOrAnUnmatchedBareCommand()
    {
        var response = BatteryFrame(0x07, 73, 0, 0, 0);
        var ids = new HashSet<byte> { 0x08, 0x09 };
        Assert.Same(response, Legacy17Protocol.Normalize(response, 0x09, ids, null));

        var bare = response[1..];
        Assert.Same(bare, Legacy17Protocol.Normalize(bare, 0x09, ids, static command => command == Legacy17Protocol.CmdInfo));
    }

    [Fact]
    public void BareReportWithNoChecksummedAllowedIdRemainsInvalid()
    {
        var response = BatteryFrame(0x07, 73, 0, 0, 0);

        var normalized = Legacy17Protocol.Normalize(response[1..], 0x09, new HashSet<byte> { 0x08, 0x09 }, null);

        Assert.False(Legacy17Protocol.HasValidChecksum(normalized));
    }

    [Fact]
    public void LargerHidCollectionsKeepTheFirstCompleteFrame()
    {
        var response = BatteryFrame(0x08, 73, 1, 0x10, 0x04);
        var padded = Enumerable.Repeat((byte)0xA5, 49).ToArray();
        response.CopyTo(padded, 0);

        Assert.Same(padded, Legacy17Protocol.Normalize(padded, 0x09, new HashSet<byte> { 0x08, 0x09 }, null));
        Assert.Equal(Legacy17Protocol.ParseBatteryPayload(response), Legacy17Protocol.ParseBatteryPayload(padded));
    }

    [Fact]
    public void BuildersNeverOverwritePayloadWithTheChecksum()
    {
        var payload = Enumerable.Range(1, 14).Select(value => (byte)value).ToArray();
        var packet = Legacy17Protocol.BuildPacket(0x08, Legacy17Protocol.CmdBattery, payload);

        Assert.Equal(payload, packet[2..16]);
        Assert.True(Legacy17Protocol.HasValidChecksum(packet));
        Assert.Throws<ArgumentOutOfRangeException>(() => Legacy17Protocol.BuildPacket(0x08, Legacy17Protocol.CmdBattery, new byte[15]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Legacy17Protocol.BuildEepromReadPacket(0x08, 0, 11));
    }

    [Fact]
    public void EepromPairsRejectTruncatedFramesAndUnboundedPairCounts()
    {
        byte[] truncated = [0x08, Legacy17Protocol.CmdGetEeprom, 0, 0, 0, 10];

        Assert.Null(Legacy17Protocol.ParseEepromPairs(truncated, 5));
        Assert.Null(Legacy17Protocol.ParseEepromPairs(truncated, -1));
        Assert.Null(Legacy17Protocol.ParseEepromPairs(truncated, int.MaxValue));

        var values = new byte[] { 0, 1, 2, 0x55, 0xFF };
        var pairs = values.SelectMany(Legacy17Protocol.EncodeCheckedValue).ToArray();
        var complete = Legacy17Protocol.BuildCommandPacket(0x08, Legacy17Protocol.CmdGetEeprom, pairs);
        Assert.Equal(values, Legacy17Protocol.ParseEepromPairs(complete, 5));
    }

    [Fact]
    public void EepromPairsRejectInvalidInternalChecksEvenWithAValidFrameChecksum()
    {
        var response = Legacy17Protocol.BuildCommandPacket(0x08, Legacy17Protocol.CmdGetEeprom, [5, 0x51]);

        Assert.Null(Legacy17Protocol.ParseEepromPairs(response, 1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void DpiParserRejectsInvalidStageIndicesWithoutIndexingTheFrame(int stage)
    {
        var response = DpiFrame([7, 7, 0, 0x47]);

        Assert.Null(Legacy17Protocol.ParseDpiStage(response, stage, 50));
    }

    [Fact]
    public void DpiParserRequiresCompleteStageAndMatchingAxes()
    {
        var response = DpiFrame([7, 7, 0, 0x47, 31, 31, 0, 0x17]);
        Assert.Equal(400, Legacy17Protocol.ParseDpiStage(response, 0, 50));
        Assert.Equal(1600, Legacy17Protocol.ParseDpiStage(response, 1, 50));

        Assert.Null(Legacy17Protocol.ParseDpiStage(response[..10], 0, 50));
        Assert.Null(Legacy17Protocol.ParseDpiStage(DpiFrame([7, 7, 0, 0x47]), 1, 50));
        Assert.Null(Legacy17Protocol.ParseDpiStage(DpiFrame([7, 8, 0, 0x46]), 0, 50));
        Assert.Null(Legacy17Protocol.ParseDpiStage(DpiFrame([7, 7, 0x10, 0x37]), 0, 50));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(536870962)]
    public void DpiParserRejectsInvalidOrOverflowingBaseSteps(int baseStep)
    {
        Assert.Null(Legacy17Protocol.ParseDpiStage(DpiFrame([7, 7, 0, 0x47]), 0, baseStep));
    }

    [Fact]
    public void VersionAndSignalRejectCorruptOrUnrelatedFrames()
    {
        var version = Legacy17Protocol.BuildCommandPacket(0x08, Legacy17Protocol.CmdVersion, [3, 0x04]);
        var dongleVersion = Legacy17Protocol.BuildCommandPacket(0x08, Legacy17Protocol.CmdDongleVersion, [2, 0x10]);
        var signal = Legacy17Protocol.BuildCommandPacket(0x08, Legacy17Protocol.CmdRssi, [4]);

        Assert.Equal("03.04", Legacy17Protocol.ParseVersionPayload(version));
        Assert.Equal("02.10", Legacy17Protocol.ParseVersionPayload(dongleVersion));
        Assert.Equal(4, Legacy17Protocol.ParseSignalPayload(signal));
        Assert.Null(Legacy17Protocol.ParseSignalPayload(version));
        Assert.Null(Legacy17Protocol.ParseVersionPayload(signal));

        version[16] ^= 1;
        signal[16] ^= 1;
        Assert.Null(Legacy17Protocol.ParseVersionPayload(version));
        Assert.Null(Legacy17Protocol.ParseSignalPayload(signal));
    }

    [Fact]
    public void InfoRejectsTruncatedAndCorruptCopiesOfTheGoldenFrame()
    {
        var challenge = Convert.FromHexString("55FE5AAE");
        var response = Convert.FromHexString("080100000008AA57BC0D5701050000001D");

        Assert.Null(Legacy17Protocol.ParseInfoPayload(response[..14], challenge));
        response[16] ^= 1;
        Assert.Null(Legacy17Protocol.ParseInfoPayload(response, challenge));
    }

    [Fact]
    public void NullInputsAreRejectedByAllParsers()
    {
        Assert.Null(Legacy17Protocol.ParseBatteryPayload(null!));
        Assert.Null(Legacy17Protocol.ParseVersionPayload(null!));
        Assert.Null(Legacy17Protocol.ParseSignalPayload(null!));
        Assert.Null(Legacy17Protocol.ParseInfoPayload(null!, new byte[4]));
        Assert.Null(Legacy17Protocol.ParseEepromPairs(null!, 1));
        Assert.Null(Legacy17Protocol.ParseDpiStage(null!, 0, 50));
        Assert.Null(Legacy17DpiCodec.DecodeStage(Legacy17SensorKind.PulsarXs1, null!));
    }

    private static byte[] BatteryFrame(byte reportId, byte battery, byte charging, byte voltageHigh, byte voltageLow) =>
        Legacy17Protocol.BuildCommandPacket(reportId, Legacy17Protocol.CmdBattery, [battery, charging, voltageHigh, voltageLow]);

    private static byte[] DpiFrame(byte[] data) =>
        Legacy17Protocol.BuildCommandPacket(0x08, Legacy17Protocol.CmdGetEeprom, data);

    private static void RecalculateChecksum(byte[] frame) =>
        frame[16] = Legacy17Protocol.Checksum(frame.AsSpan(0, 16));
}
