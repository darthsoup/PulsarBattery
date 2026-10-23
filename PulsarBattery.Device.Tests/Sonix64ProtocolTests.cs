using PulsarBattery.Device;

namespace PulsarBattery.Device.Tests;

public sealed class Sonix64ProtocolTests
{
    [Fact]
    public void BatteryRequestUsesReportIdPrefixAndLittleEndianChecksum()
    {
        var expected = Convert.FromHexString("0000088101" + new string('0', 116) + "8A00");

        Assert.Equal(expected, Sonix64Protocol.BuildPacket([0x08, 0x81, 0x01]));
    }

    [Fact]
    public void DpiWritePreservesProfileAndBothAxes()
    {
        var expected = Convert.FromHexString("000005020500000190019001" + new string('0', 102) + "2F01");

        Assert.Equal(expected, Sonix64Protocol.BuildWritePacket([0x05, 0x02, 0x05], [0x90, 0x01, 0x90, 0x01]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(64)]
    public void BuildersRejectMalformedCommands(int length)
    {
        var command = new byte[length];

        Assert.Throws<ArgumentException>(() => Sonix64Protocol.BuildPacket(command));
        Assert.Throws<ArgumentException>(() => Sonix64Protocol.BuildWritePacket(command, [1]));
    }

    [Fact]
    public void BuildersRejectNullCommands()
    {
        Assert.Throws<ArgumentNullException>(() => Sonix64Protocol.BuildPacket(null!));
        Assert.Throws<ArgumentNullException>(() => Sonix64Protocol.BuildWritePacket(null!, [1]));
    }

    [Fact]
    public void WritePayloadCannotOverwriteChecksum()
    {
        byte[] command = [0x05, 0x02, 0x05];
        var packet = Sonix64Protocol.BuildWritePacket(command, Enumerable.Repeat((byte)0xFF, 55).ToArray());

        Assert.Equal(65, packet.Length);
        Assert.Equal(0xFF, packet[62]);
        Assert.Equal(14_038, packet[63] | (packet[64] << 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sonix64Protocol.BuildWritePacket(command, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sonix64Protocol.BuildWritePacket(command, new byte[56]));
    }

    [Fact]
    public void ResponseCorrelationPreservesPayload()
    {
        byte[] command = [0x08, 0x81, 0x01];
        var response = BuildResponse(command, 0x01);
        response[7] = 73;

        Assert.True(Sonix64Protocol.TryParseResponse(response, command, false, out var wire));
        Assert.Equal(64, wire.Length);
        Assert.Equal(73, wire[6]);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x02)]
    [InlineData(0x05)]
    [InlineData(0xFF)]
    public void ReadsRejectPendingAndWriteOnlyResponses(byte direction)
    {
        byte[] command = [0x08, 0x81, 0x01];

        Assert.False(Sonix64Protocol.TryParseResponse(BuildResponse(command, direction), command, false, out var wire));
        Assert.Empty(wire);
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x02)]
    public void WritesAcceptBothExistingAcknowledgementDirections(byte direction)
    {
        byte[] command = [0x07, 0x05, 0x02];

        Assert.True(Sonix64Protocol.TryParseResponse(BuildResponse(command, direction), command, true, out _));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ResponsesForDifferentRegistersAreRejected(int index)
    {
        byte[] command = [0x08, 0x81, 0x01];
        var response = BuildResponse(command, 0x01);
        response[index] ^= 0x01;

        Assert.False(Sonix64Protocol.TryParseResponse(response, command, false, out var wire));
        Assert.Empty(wire);
        Assert.False(Sonix64Protocol.TryParseResponse(response, command, true, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(63)]
    [InlineData(64)]
    public void TruncatedResponsesAreRejected(int length)
    {
        Assert.False(Sonix64Protocol.TryParseResponse(new byte[length], [0x08, 0x81, 0x01], false, out var wire));
        Assert.Empty(wire);
    }

    [Fact]
    public void InvalidExpectedCommandsAreRejectedWithoutIndexing()
    {
        var response = new byte[65];

        Assert.False(Sonix64Protocol.TryParseResponse(response, [], false, out _));
        Assert.False(Sonix64Protocol.TryParseResponse(response, [0x08, 0x81], false, out _));
        Assert.False(Sonix64Protocol.TryParseResponse(response, null!, false, out _));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void BooleanSettingsAcceptOnlyKnownValues(byte value, bool expected)
        => Assert.Equal(expected, Sonix64Protocol.ParseBoolean(value));

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(255)]
    public void MalformedBooleanSettingsRemainUnknown(byte value)
        => Assert.Null(Sonix64Protocol.ParseBoolean(value));

    private static byte[] BuildResponse(byte[] command, byte direction)
    {
        var response = new byte[65];
        response[1] = direction;
        command.CopyTo(response, 2);
        return response;
    }
}
