using PulsarBattery.Device;

namespace PulsarBattery.Device.Tests;

public sealed class CmouseDeviceCatalogTests
{
    [Fact]
    public void V131CatalogHasExpectedUsbAndIdentityCoverage()
    {
        Assert.Equal(0x3710, CmouseDeviceCatalog.VendorId);
        Assert.Equal(0x57, CmouseDeviceCatalog.Cid);
        Assert.Equal(50, CmouseDeviceCatalog.ProductIds.Count);
        Assert.Contains(0x3414, CmouseDeviceCatalog.ProductIds);
        Assert.Contains(0x5406, CmouseDeviceCatalog.ProductIds);

        Assert.Equal(125, CmouseDeviceCatalog.Profiles.Count);
        Assert.Equal(
            Enumerable.Range(1, 125).Select(value => (byte)value),
            CmouseDeviceCatalog.Profiles.Select(profile => profile.Mid).Order());
        Assert.All(CmouseDeviceCatalog.Profiles, profile =>
        {
            Assert.Equal(CmouseDeviceCatalog.Cid, profile.Cid);
            Assert.False(string.IsNullOrWhiteSpace(profile.Model));
        });
    }

    [Fact]
    public void V131SensorDistributionMatchesConfigIni()
    {
        var bySensor = CmouseDeviceCatalog.Profiles
            .GroupBy(profile => profile.Sensor)
            .ToDictionary(group => group.Key, group => group.Count());

        Assert.Equal(102, bySensor[Legacy17SensorKind.PulsarXs1]);
        Assert.Equal(8, bySensor[Legacy17SensorKind.Paw3950]);
        Assert.Equal(15, bySensor[Legacy17SensorKind.Paw3955]);
    }

    [Fact]
    public void EveryCatalogProfileHasWriteSupportWithExplicitTrust()
    {
        Assert.All(CmouseDeviceCatalog.Profiles, profile => Assert.True(profile.WriteEnabled));
        Assert.Equal(
            124,
            CmouseDeviceCatalog.Profiles.Count(profile =>
                profile.WriteTrust == DeviceSettingsWriteTrust.VendorDerived));
        Assert.DoesNotContain(
            CmouseDeviceCatalog.Profiles,
            profile => profile.WriteTrust == DeviceSettingsWriteTrust.Unavailable);
    }

    [Fact]
    public void LiveCrazyLightProfileIsOnlyHardwareWriteVerifiedEntry()
    {
        Assert.True(CmouseDeviceCatalog.TryGet(0x57, 0x01, out var profile));
        Assert.Equal("X2 CrazyLight", profile.Model);
        Assert.Equal(Legacy17SensorKind.PulsarXs1, profile.Sensor);
        Assert.True(profile.WriteEnabled);
        Assert.True(profile.HardwareWriteVerified);
        Assert.Equal(DeviceSettingsWriteTrust.HardwareVerified, profile.WriteTrust);

        var writeVerified = CmouseDeviceCatalog.Profiles.Where(item => item.HardwareWriteVerified).ToArray();
        Assert.Single(writeVerified);
        Assert.Equal(0x01, writeVerified[0].Mid);
    }

    [Fact]
    public void CatalogRejectsUnknownCidOrMid()
    {
        Assert.False(CmouseDeviceCatalog.TryGet(0x56, 0x01, out _));
        Assert.False(CmouseDeviceCatalog.TryGet(0x57, 0x00, out _));
        Assert.False(CmouseDeviceCatalog.TryGet(0x57, 0xFF, out _));
    }

    [Fact]
    public void DiscreteLodEncodingMatchesXs1AndPaw3950Ui()
    {
        Assert.True(CmouseDeviceCatalog.TryGet(0x57, 0x01, out var profile));
        Assert.Equal(new[] { 7, 10, 20 }, CmouseLegacyBackend.GetLodValues(profile));
        Assert.Equal(7, CmouseLegacyBackend.DecodeLodMm10(profile, 3));
        Assert.Equal(10, CmouseLegacyBackend.DecodeLodMm10(profile, 1));
        Assert.Equal(20, CmouseLegacyBackend.DecodeLodMm10(profile, 2));
        Assert.Equal(3, CmouseLegacyBackend.EncodeLodCode(profile, 7));
        Assert.Equal(1, CmouseLegacyBackend.EncodeLodCode(profile, 10));
        Assert.Equal(2, CmouseLegacyBackend.EncodeLodCode(profile, 20));
    }

    [Fact]
    public void ContinuousLodEncodingMatchesPaw3955Ui()
    {
        Assert.True(CmouseDeviceCatalog.TryGet(0x57, 101, out var profile));
        Assert.Equal(Enumerable.Range(7, 11), CmouseLegacyBackend.GetLodValues(profile));
        for (byte code = 1; code <= 11; code++)
        {
            var mm10 = code + 6;
            Assert.Equal(mm10, CmouseLegacyBackend.DecodeLodMm10(profile, code));
            Assert.Equal(code, CmouseLegacyBackend.EncodeLodCode(profile, mm10));
        }

        Assert.Null(CmouseLegacyBackend.DecodeLodMm10(profile, 0));
        Assert.Null(CmouseLegacyBackend.DecodeLodMm10(profile, 12));
    }
}
