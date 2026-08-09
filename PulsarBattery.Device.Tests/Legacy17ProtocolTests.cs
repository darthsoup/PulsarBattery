using PulsarBattery.Device;

namespace PulsarBattery.Device.Tests;

public sealed class Legacy17ProtocolTests
{
    private const byte ReportId = Legacy17Protocol.OutputReportId;

    [Fact]
    public void DriverStatusFramesMatchCmouseV131()
    {
        Assert.Equal(
            Convert.FromHexString("0802000000010100000000000000000049"),
            Legacy17Protocol.BuildDriverStatusPacket(ReportId, online: true));
        Assert.Equal(
            Convert.FromHexString("080200000001000000000000000000004A"),
            Legacy17Protocol.BuildDriverStatusPacket(ReportId, online: false));
    }

    [Fact]
    public void OnlineAndHoldFramesUsePlainLengths()
    {
        Assert.Equal(
            Convert.FromHexString("080300000000000000000000000000004A"),
            Legacy17Protocol.BuildOnlinePacket(ReportId));
        Assert.Equal(
            Convert.FromHexString("0803000000010100000000000000000048"),
            Legacy17Protocol.BuildOnlinePacket(ReportId, hold: true));
        Assert.Equal(
            Convert.FromHexString("0803000000010000000000000000000049"),
            Legacy17Protocol.BuildOnlinePacket(ReportId, hold: false));

        Assert.Equal(0x00, Legacy17Protocol.BuildOnlinePacket(ReportId)[5]);
        Assert.Equal(0x01, Legacy17Protocol.BuildOnlinePacket(ReportId, true)[5]);
    }

    [Fact]
    public void LiveCrazyLightV304InfoVectorDecodesCidMidAnd8KDongle()
    {
        var challenge = Convert.FromHexString("55FE5AAE");
        var response = Convert.FromHexString("080100000008AA57BC0D5701050000001D");

        Assert.True(Legacy17Protocol.HasValidChecksum(response));
        Assert.Equal(
            Convert.FromHexString("08010000000855FE5AAE000000000000E9"),
            Legacy17Protocol.BuildInfoPacket(ReportId, challenge));

        var parsed = Legacy17Protocol.ParseInfoPayload(response, challenge);

        Assert.NotNull(parsed);
        Assert.Equal(0x5701, parsed.Value.ModelId);
        Assert.Equal(Legacy17Protocol.Connection8K, parsed.Value.ConnectionCode);
        Assert.Equal(0x00, parsed.Value.DongleType);
    }

    [Fact]
    public void InfoParserRejectsCidMidThatDoNotMatchMixedCopy()
    {
        var challenge = Convert.FromHexString("55FE5AAE");
        var response = Convert.FromHexString("080100000008AA57BC0D5701050000001D");
        response[10] ^= 0x01;
        RecalculateChecksum(response);

        Assert.Null(Legacy17Protocol.ParseInfoPayload(response, challenge));
    }

    [Fact]
    public void EepromReadAndWriteFramesMatchGoldenVectors()
    {
        Assert.Equal(
            Convert.FromHexString("08080000A902000000000000000000009A"),
            Legacy17Protocol.BuildEepromReadPacket(ReportId, 0x00A9, 2));
        Assert.Equal(
            Convert.FromHexString("08070000A9020550000000000000000046"),
            Legacy17Protocol.BuildEepromWritePacket(
                ReportId,
                0x00A9,
                Legacy17Protocol.EncodeCheckedValue(5)));
    }

    [Fact]
    public void BuildersRejectDataBeyondFrameCapacity()
    {
        var maximum = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();
        var tooLarge = new byte[11];

        var command = Legacy17Protocol.BuildCommandPacket(ReportId, Legacy17Protocol.CmdBattery, maximum);
        var write = Legacy17Protocol.BuildEepromWritePacket(ReportId, 0x1234, maximum);
        Assert.Equal(0x0A, command[5]);
        Assert.Equal(0x0A, write[5]);
        Assert.True(Legacy17Protocol.HasValidChecksum(command));
        Assert.True(Legacy17Protocol.HasValidChecksum(write));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Legacy17Protocol.BuildCommandPacket(ReportId, Legacy17Protocol.CmdBattery, tooLarge));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Legacy17Protocol.BuildEepromWritePacket(ReportId, 0, tooLarge));
    }

    [Theory]
    [InlineData(0x00, 0x55)]
    [InlineData(0x55, 0x00)]
    [InlineData(0xFF, 0x56)]
    public void CheckedScalarSumsTo55Modulo256(byte value, byte check)
    {
        var encoded = Legacy17Protocol.EncodeCheckedValue(value);

        Assert.Equal(new[] { value, check }, encoded);
        Assert.Equal(0x55, (encoded[0] + encoded[1]) & 0xFF);
    }

    [Fact]
    public void ChecksumUsesFirst17BytesAndRejectsShortOrCorruptFrames()
    {
        var frame = Legacy17Protocol.BuildDriverStatusPacket(ReportId, true);
        var padded = Enumerable.Repeat((byte)0xA5, 49).ToArray();
        frame.CopyTo(padded, 0);

        Assert.True(Legacy17Protocol.HasValidChecksum(frame));
        Assert.True(Legacy17Protocol.HasValidChecksum(padded));
        Assert.False(Legacy17Protocol.HasValidChecksum(frame[..16]));

        frame[16] ^= 0x01;
        Assert.False(Legacy17Protocol.HasValidChecksum(frame));
    }

    [Fact]
    public void EepromParserRequiresChecksumStatusCommandAddressAndExactLength()
    {
        const ushort address = 0x1234;
        byte[] expected = [0x10, 0x20, 0x30, 0x40];
        var valid = BuildEepromResponse(Legacy17Protocol.CmdGetEeprom, address, expected);

        Assert.True(Legacy17Protocol.TryParseEepromResponse(
            valid,
            Legacy17Protocol.CmdGetEeprom,
            address,
            expected.Length,
            out var parsed));
        Assert.Equal(expected, parsed);

        var corruptChecksum = valid.ToArray();
        corruptChecksum[16] ^= 0x01;

        var statusOnly = valid.ToArray();
        statusOnly[2] = 0x01;
        RecalculateChecksum(statusOnly);

        var wrongCommand = valid.ToArray();
        wrongCommand[1] = Legacy17Protocol.CmdSetEeprom;
        RecalculateChecksum(wrongCommand);

        var wrongAddress = valid.ToArray();
        wrongAddress[4] ^= 0x01;
        RecalculateChecksum(wrongAddress);

        var wrongLength = valid.ToArray();
        wrongLength[5] = 3;
        RecalculateChecksum(wrongLength);

        foreach (var invalid in new[]
                 {
                     corruptChecksum,
                     statusOnly,
                     wrongCommand,
                     wrongAddress,
                     wrongLength,
                 })
        {
            Assert.False(Legacy17Protocol.TryParseEepromResponse(
                invalid,
                Legacy17Protocol.CmdGetEeprom,
                address,
                expected.Length,
                out var rejected));
            Assert.Empty(rejected);
        }
    }

    [Fact]
    public void WriteAckMustBeExactChecksummedEcho()
    {
        const ushort address = 0x00A9;
        var data = Legacy17Protocol.EncodeCheckedValue(5);
        var request = Legacy17Protocol.BuildEepromWritePacket(ReportId, address, data);
        var paddedResponse = new byte[49];
        request.CopyTo(paddedResponse, 0);

        Assert.True(Legacy17Protocol.MatchesWriteAck(request, request));
        Assert.True(Legacy17Protocol.MatchesWriteAck(request, address, data));
        Assert.True(Legacy17Protocol.MatchesWriteAck(paddedResponse, address, data));

        var differentData = request.ToArray();
        differentData[6] ^= 0x01;
        RecalculateChecksum(differentData);
        Assert.False(Legacy17Protocol.MatchesWriteAck(differentData, request));

        var corruptChecksum = request.ToArray();
        corruptChecksum[16] ^= 0x01;
        Assert.False(Legacy17Protocol.MatchesWriteAck(corruptChecksum, address, data));
    }

    private static byte[] BuildEepromResponse(byte command, ushort address, byte[] data)
    {
        var frame = new byte[17];
        frame[0] = ReportId;
        frame[1] = command;
        frame[3] = (byte)(address >> 8);
        frame[4] = (byte)address;
        frame[5] = (byte)data.Length;
        data.CopyTo(frame, 6);
        RecalculateChecksum(frame);
        return frame;
    }

    private static void RecalculateChecksum(byte[] frame) =>
        frame[16] = Legacy17Protocol.Checksum(frame.AsSpan(0, 16));
}
