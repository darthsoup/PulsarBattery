using System.Buffers.Binary;
using PulsarBattery.Services;

namespace PulsarBattery.Device.Tests;

public sealed class DeviceImageServiceTests : IDisposable
{
    private const string ValidRgbaPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";
    private const string ValidIndexedPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABAQMAAAAl21bKAAAAA1BMVEX/AAAZ4gk3AAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==";
    private const string NoncontiguousIdatAfterEmptyPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAElEQVQ1rwYeAAAAA3RFWHRrAHbLBPOQAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";
    private const string IdatBeforePalettePng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAAAElEQVQ1rwYeAAAAA1BMVEX/AAAZ4gk3AAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC";
    private const string UnknownCriticalChunkPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAVRFU1QAF1Y+oAAAAA1JREFUeJxj+M/A8B8ABQAB/4mZPR0AAAAASUVORK5CYII=";
    private const string InvalidRgbaBitDepthPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABAQYAAAASBab4AAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";
    private const string OversizedWidthPng =
        "iVBORw0KGgoAAAANSUhEUgAAEAEAAAABCAYAAACx4wBCAAAAKklEQVR4nO3DQQ0AAAwDofNvupOxDyS0mqqqqqqqqqqqqqqqqqqqqvr/AITl49Ak3alAAAAAAElFTkSuQmCC";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "PulsarBatteryImageTests-" + Guid.NewGuid().ToString("N"));

    public DeviceImageServiceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Theory]
    [InlineData("X2-CrazyLight.png")]
    [InlineData("X2v1.png")]
    [InlineData("X2v3-eS.png")]
    public void PackagedArtworkWithRealMetadataIsAccepted(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Devices", fileName);

        Assert.True(File.Exists(path), $"Missing copied artwork fixture: {path}");
        Assert.True(DeviceImageService.HasValidPng(path));
    }

    [Theory]
    [InlineData(ValidRgbaPng)]
    [InlineData(ValidIndexedPng)]
    public void CompleteTruecolorAndIndexedImagesAreAccepted(string base64)
    {
        Assert.True(Validate(Convert.FromBase64String(base64)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(33)]
    [InlineData(45)]
    [InlineData(69)]
    public void TruncationAtHeadersImageDataAndTrailerIsRejected(int length)
    {
        var png = Convert.FromBase64String(ValidRgbaPng);

        Assert.False(Validate(png[..length]));
    }

    [Theory]
    [InlineData(41)]
    [InlineData(69)]
    public void CorruptedImageDataOrChecksumIsRejected(int offset)
    {
        var png = Convert.FromBase64String(ValidRgbaPng);
        png[offset] ^= 1;

        Assert.False(Validate(png));
    }

    [Theory]
    [InlineData(NoncontiguousIdatAfterEmptyPng)]
    [InlineData(IdatBeforePalettePng)]
    public void EmptyImageDataChunksStillEnforceOrdering(string base64)
    {
        Assert.False(Validate(Convert.FromBase64String(base64)));
    }

    [Fact]
    public void ContiguousEmptyImageDataChunksAreAccepted()
    {
        var png = Convert.FromBase64String(ValidRgbaPng);
        var emptyIdat = Convert.FromHexString("000000004944415435AF061E");

        Assert.True(Validate([.. png[..33], .. emptyIdat, .. png[33..58], .. emptyIdat, .. png[58..]]));
    }

    [Theory]
    [InlineData(UnknownCriticalChunkPng)]
    [InlineData(InvalidRgbaBitDepthPng)]
    [InlineData(OversizedWidthPng)]
    public void CrcValidButUnsupportedImagesAreRejected(string base64)
    {
        Assert.False(Validate(Convert.FromBase64String(base64)));
    }

    [Fact]
    public void IndexedImageWithoutItsPaletteIsRejected()
    {
        var png = Convert.FromBase64String(ValidIndexedPng);

        Assert.False(Validate([.. png[..33], .. png[48..]]));
    }

    [Fact]
    public void UntrustedChunkLengthCannotOverflowOrReadPastTheFile()
    {
        var png = Convert.FromBase64String(ValidRgbaPng);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(33, 4), uint.MaxValue);

        Assert.False(Validate(png));
    }

    [Fact]
    public void TrailingBytesAfterTheImageTrailerAreRejected()
    {
        var png = Convert.FromBase64String(ValidRgbaPng);

        Assert.False(Validate([.. png, 0]));
    }

    [Fact]
    public void InvalidSignatureIsRejected()
    {
        var png = Convert.FromBase64String(ValidRgbaPng);
        png[0] = 0;

        Assert.False(Validate(png));
    }

    [Fact]
    public void OversizedCacheFileIsRejected()
    {
        var path = Path.Combine(_directory, "oversized.png");
        using (var stream = File.Create(path))
        {
            stream.Write(Convert.FromBase64String(ValidRgbaPng));
            stream.SetLength(3 * 1024 * 1024 + 1);
        }

        Assert.False(DeviceImageService.HasValidPng(path));
    }

    [Fact]
    public void MissingFileAndDirectoryAreRejectedWithoutThrowing()
    {
        Assert.False(DeviceImageService.HasValidPng(Path.Combine(_directory, "missing.png")));
        Assert.False(DeviceImageService.HasValidPng(_directory));
    }

    [Theory]
    [InlineData(" x2 v1 ", "ms-appx:///Assets/Devices/X2v1.png")]
    [InlineData("x2 v3 ES", "ms-appx:///Assets/Devices/X2v3-eS.png")]
    [InlineData("X2 CRAZYLIGHT", "ms-appx:///Assets/Devices/X2-CrazyLight.png")]
    public void PackagedFallbackIgnoresModelCaseAndWhitespace(string model, string expected)
    {
        Assert.Equal(new Uri(expected), DeviceImageService.GetPackagedImage(model));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyModelHasNoPackagedFallback(string? model)
    {
        Assert.Null(DeviceImageService.GetPackagedImage(model!));
    }

    [Fact]
    public async Task CancelledImageRequestReturnsWithoutStartingDownload()
    {
        Assert.Null(await DeviceImageService.GetCachedOfficialImageAsync(
            "X2 CrazyLight", null, new CancellationToken(canceled: true)));
    }

    private bool Validate(byte[] bytes)
    {
        var path = Path.Combine(_directory, "image.png");
        File.WriteAllBytes(path, bytes);
        return DeviceImageService.HasValidPng(path);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
