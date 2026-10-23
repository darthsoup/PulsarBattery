using PulsarBattery.Device;

namespace PulsarBattery.Device.Tests;

public sealed class X2TargetSelectionTests
{
    private const int WirelessProductId = 0xFA7C;
    private const int WiredProductId = 0xFA7B;

    [Fact]
    public void BackendRequiresSettingsDiscoveryBeforeWriting()
    {
        var backend = new X2V1Backend();

        Assert.False(backend.SupportsSettingsWrite);
        Assert.False(backend.ApplySettings(new DeviceSettings(Dpi: 800), false));
    }

    [Fact]
    public void EmptyEnumerationHasNoTarget()
        => Assert.Null(X2V1Backend.SelectSettingsTarget([]));

    [Fact]
    public void OneEndpointCanSupplyBothReportDirections()
    {
        var target = X2V1Backend.SelectSettingsTarget([
            new("shared", WirelessProductId, 17, 17),
        ]);

        Assert.Equal(new X2V1Backend.Target("shared", "shared"), target);
    }

    [Fact]
    public void UniqueSplitEndpointsOfSameProductAreSelected()
    {
        var target = X2V1Backend.SelectSettingsTarget([
            new("reader", WirelessProductId, 0, 17),
            new("unrelated", WiredProductId, 64, 64),
            new("writer", WirelessProductId, 17, 0),
        ]);

        Assert.Equal(new X2V1Backend.Target("writer", "reader"), target);
    }

    [Fact]
    public void DuplicatePathsIgnoreCaseWithoutCreatingAmbiguity()
    {
        var target = X2V1Backend.SelectSettingsTarget([
            new("writer", WirelessProductId, 17, 0),
            new("WRITER", WirelessProductId, 17, 0),
            new("reader", WirelessProductId, 0, 17),
            new("READER", WirelessProductId, 0, 17),
        ]);

        Assert.NotNull(target);
        Assert.Equal("writer", target.WriterPath, ignoreCase: true);
        Assert.Equal("reader", target.ReaderPath, ignoreCase: true);
    }

    [Theory]
    [InlineData(17, 0)]
    [InlineData(0, 17)]
    [InlineData(17, 17)]
    public void AdditionalEligibleEndpointMakesDiscoveryAmbiguous(int featureLength, int inputLength)
    {
        var target = X2V1Backend.SelectSettingsTarget([
            new("writer", WirelessProductId, 17, 0),
            new("reader", WirelessProductId, 0, 17),
            new("additional", WirelessProductId, featureLength, inputLength),
        ]);

        Assert.Null(target);
    }

    [Fact]
    public void DifferentProductIdsCannotFormTarget()
    {
        var target = X2V1Backend.SelectSettingsTarget([
            new("writer", WirelessProductId, 17, 0),
            new("reader", WiredProductId, 0, 17),
        ]);

        Assert.Null(target);
    }

    [Theory]
    [InlineData(0, 17)]
    [InlineData(16, 17)]
    [InlineData(18, 17)]
    [InlineData(17, 0)]
    [InlineData(17, 16)]
    [InlineData(17, 18)]
    public void BothDirectionsRequireExactReportLength(int featureLength, int inputLength)
    {
        var target = X2V1Backend.SelectSettingsTarget([
            new("candidate", WirelessProductId, featureLength, inputLength),
        ]);

        Assert.Null(target);
    }

    [Fact]
    public void PinnedPathsRemainSelectedAmongOtherDevicesUsingEnumeratedCasing()
    {
        var target = X2V1Backend.SelectSettingsTarget([
            new("other", WiredProductId, 17, 17),
            new("WRITER", WirelessProductId, 17, 0),
            new("READER", WirelessProductId, 0, 17),
            new("another", WirelessProductId, 17, 17),
        ], new("writer", "reader"));

        Assert.Equal(new X2V1Backend.Target("WRITER", "READER"), target);
    }

    [Fact]
    public void PinnedSharedEndpointRemainsSelectedAmongOtherDevices()
    {
        var target = X2V1Backend.SelectSettingsTarget([
            new("other", WirelessProductId, 17, 17),
            new("SHARED", WirelessProductId, 17, 17),
        ], new("shared", "shared"));

        Assert.Equal(new X2V1Backend.Target("SHARED", "SHARED"), target);
    }

    [Theory]
    [InlineData("missing-writer", "reader")]
    [InlineData("writer", "missing-reader")]
    [InlineData("missing-writer", "missing-reader")]
    public void MissingPinnedEndpointNeverFallsBackToAvailableTarget(string writerPath, string readerPath)
    {
        var target = X2V1Backend.SelectSettingsTarget([
            new("writer", WirelessProductId, 17, 0),
            new("reader", WirelessProductId, 0, 17),
        ], new(writerPath, readerPath));

        Assert.Null(target);
    }

    [Theory]
    [InlineData(16, 17)]
    [InlineData(18, 17)]
    [InlineData(17, 16)]
    [InlineData(17, 18)]
    public void PinnedEndpointsMustRetainRequiredReportCapabilities(int featureLength, int inputLength)
    {
        var target = X2V1Backend.SelectSettingsTarget([
            new("writer", WirelessProductId, featureLength, 0),
            new("reader", WirelessProductId, 0, inputLength),
            new("replacement", WirelessProductId, 17, 17),
        ], new("writer", "reader"));

        Assert.Null(target);
    }

    [Fact]
    public void PinnedEndpointsWithDifferentProductIdsAreRejected()
    {
        var target = X2V1Backend.SelectSettingsTarget([
            new("writer", WirelessProductId, 17, 0),
            new("reader", WiredProductId, 0, 17),
        ], new("writer", "reader"));

        Assert.Null(target);
    }

    [Fact]
    public void WarmupPacketPreservesCapturedLengthAndZeroReservedBytes()
    {
        var builder = typeof(X2V1Backend).GetMethod(
            "BuildCmd01Packet",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(builder);
        var packet = Assert.IsType<byte[]>(builder.Invoke(null, null));

        Assert.Equal(16, packet.Length);
        Assert.Equal(0x08, packet[0]);
        Assert.Equal(0x01, packet[1]);
        Assert.Equal(0x08, packet[5]);
        foreach (var index in new[] { 2, 3, 4, 10, 11, 12, 13, 14 })
        {
            Assert.Equal(0, packet[index]);
        }

        Assert.Equal(0x55, packet.Sum(value => (int)value) & 0xFF);
    }
}
