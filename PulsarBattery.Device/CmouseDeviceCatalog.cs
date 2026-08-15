using System.Collections.Generic;
using System.Linq;

namespace PulsarBattery.Device;

internal enum Legacy17SensorKind
{
    PulsarXs1,
    Paw3950,
    Paw3955,
}

internal sealed record CmouseDeviceProfile(
    byte Cid,
    byte Mid,
    string Model,
    Legacy17SensorKind Sensor,
    DeviceSettingsWriteTrust WriteTrust = DeviceSettingsWriteTrust.VendorDerived)
{
    public bool WriteEnabled => WriteTrust is not DeviceSettingsWriteTrust.Unavailable;

    public bool HardwareWriteVerified => WriteTrust is DeviceSettingsWriteTrust.HardwareVerified;
}

internal static class CmouseDeviceCatalog
{
    public const int VendorId = 0x3710;
    public const byte Cid = 0x57;

    // Source: cMouse V1.31 usersetting/Config.ini; CID/MID cross-checked against its 0x01 response handling.
    public static IReadOnlySet<int> ProductIds { get; } = new HashSet<int>
    {
        0x3415,
        0x3501,
        0x3502,
        0x3503,
        0x3504,
        0x3505,
        0x3506,
        0x3507,
        0x3508,
        0x3509,
        0x3510,
        0x3511,
        0x3512,
        0x3513,
        0x3514,
        0x3515,
        0x3516,
        0x3517,
        0x3518,
        0x3519,
        0x7505,
        0x3414,
        0x7501,
        0x7503,
        0x7504,
        0x5502,
        0x5501,
        0x7506,
        0x3524,
        0x3525,
        0x3527,
        0x3526,
        0x3528,
        0x3529,
        0x9502,
        0x7509,
        0x7510,
        0x7508,
        0x5407,
        0x7502,
        0x7507,
        0x5504,
        0x7602,
        0x9503,
        0x7601,
        0x9601,
        0x7603,
        0x3603,
        0x5601,
        0x5406,
    };

    public static IReadOnlyList<CmouseDeviceProfile> Profiles { get; } =
    [
        Xs1(1, "X2 CrazyLight", DeviceSettingsWriteTrust.HardwareVerified),
        Xs1(2, "X2 CrazyLight"),
        Xs1(3, "X2 CrazyLight"),
        Xs1(4, "X2 CrazyLight"),
        Xs1(5, "X2 CrazyLight"),
        Xs1(6, "X2 CrazyLight"),
        Xs1(7, "Tenz"),
        Xs1(8, "Tenz"),
        Xs1(9, "X2 CrazyLight"),
        Xs1(10, "X2 CrazyLight"),
        Xs1(11, "Pulsar X VSPO! Aizawa Ema"),
        Xs1(12, "X2 CrazyLight T1 Edition(Red)"),
        Xs1(13, "X2 CrazyLight T1 Edition(Black)"),
        Xs1(14, "X2 CrazyLight PRX Edition"),
        Xs1(15, "X2 CrazyLight Boardzy Edition"),
        Xs1(16, "X2 CrazyLight Randomfrankp Edition"),
        Xs1(17, "Xlite CrazyLight (Black)"),
        Xs1(18, "Xlite CrazyLight (White)"),
        Xs1(19, "X3 CrazyLight (Black)"),
        Xs1(20, "X3 CrazyLight (White)"),
        Xs1(21, "X3 LHD CrazyLight (Black)"),
        Xs1(22, "X3 LHD CrazyLight (White)"),
        Xs1(23, "X2H CrazyLight (Black)"),
        Xs1(24, "X2H CrazyLight (White)"),
        Xs1(25, "X2N CrazyLight (Black)"),
        Xs1(26, "X2N CrazyLight (White)"),
        Xs1(27, "X2 CrazyLight Medium"),
        Xs1(28, "X2 Mini Pulsar By You XS-1"),
        Xs1(29, "X2H CrazyLight Medium"),
        Xs1(30, "X2H Mini Pulsar By You XS-1"),
        Xs1(31, "X2A Medium Pulsar By You XS-1"),
        Xs1(32, "X2A Mini Pulsar By You XS-1"),
        Xs1(33, "Xlite Mini Pulsar By You XS-1"),
        Xs1(34, "Xlite CrazyLight Medium"),
        Xs1(35, "Xlite Large Pulsar By You XS-1"),
        Xs1(36, "Lab. X2F"),
        Xs1(37, "Tenz Signature Edition"),
        Xs1(38, "JV-X"),
        Xs1(39, "Susanto-X"),
        Xs1(40, "Tenz Signature Edition"),
        Xs1(41, "X2 CrazyLight 5th Anniversary Edition"),
        Xs1(42, "X2 CrazyLight Wild Scape Desert Edition"),
        Xs1(43, "X2 CrazyLight Wild Scape Forest Edition"),
        Xs1(44, "X2 CrazyLight Wild Scape Ocean Edition"),
        Xs1(45, "Pulsar X VSPO! Asumi Sena"),
        Xs1(46, "Pulsar X VSPO! Hanabusa Lisa"),
        Xs1(47, "Pulsar X VSPO! Ichinose Uruha"),
        Xs1(48, "Pulsar X VSPO! Kaga Sumire"),
        Xs1(49, "Pulsar X VSPO! Kisaragi Ren"),
        Xs1(50, "Pulsar X VSPO! Kogara Toto"),
        Xs1(51, "Pulsar X VSPO! Komori Met"),
        Xs1(52, "Pulsar X VSPO! Kurumi Noah"),
        Xs1(53, "Pulsar X VSPO! Kaga Nazuna"),
        Xs1(54, "Pulsar X VSPO! Nekota Tsuna"),
        Xs1(55, "Pulsar X VSPO! Kaminari Qpi"),
        Xs1(56, "Pulsar X VSPO! Sendo Yuuhi"),
        Xs1(57, "Pulsar X VSPO! Shinomiya Runa"),
        Xs1(58, "Pulsar X VSPO! Shiranami Ramune"),
        Xs1(59, "Pulsar X VSPO! Tachibana Hinano"),
        Xs1(60, "Pulsar X VSPO! Tosaki Mimi"),
        Xs1(61, "Pulsar X VSPO! Tsumugi Kokage"),
        Xs1(62, "Pulsar X VSPO! Yakumo Beni"),
        Xs1(63, "Pulsar X VSPO! Yano Kuromu"),
        Xs1(64, "Pulsar X VSPO! Yumeno Akari"),
        Xs1(65, "Pulsar X VSPO! Amayumi Moka"),
        Xs1(66, "Pulsar X VSPO! Choya Hanabi"),
        Xs1(67, "X2F 5th_Pulsar By You XS-1"),
        Paw3950(68, "Pulsar Zywoo The Chosen Mouse Gen.2"),
        Paw3950(69, "Pulsar Zywoo The Chosen Mouse Gen.2"),
        Xs1(70, "X2 CrazyLight Medium(White)"),
        Xs1(71, "Xlite CrazyLight Medium (White)"),
        Xs1(72, "X3 CrazyLight Medium (Black)"),
        Xs1(73, "X3 CrazyLight Medium(White)"),
        Xs1(74, "X3 LHD CrazyLight Medium (Black)"),
        Xs1(75, "X3 LHD CrazyLight Medium (White)"),
        Xs1(76, "X2H CrazyLight Medium (White)"),
        Xs1(77, "X2N CrazyLight Medium (Black)"),
        Xs1(78, "X2N CrazyLight Medium (White)"),
        Xs1(79, "X2 CrazyLight The Finals Edition Red (Medium)"),
        Xs1(80, "X2 CrazyLightThe Finals Edition Black (Medium)"),
        Xs1(81, "X2 CrazyLight Medium Bruce Lee 85th ED"),
        Xs1(82, "X2 CrazyLight Bruce Lee 85th ED"),
        Xs1(83, "X2 CrazyLight Medium PRX Pacific Gold ED"),
        Xs1(84, "X2 CrazyLight Pokemon Edition Pikachu (Medium)"),
        Xs1(85, "X2 CrazyLight Pokemon Edition Pikachu (Mini)"),
        Xs1(86, "X2 CrazyLight Medium Pokemon FF Edition"),
        Xs1(87, "X2 CrazyLight Medium Blue Archive Edition"),
        Xs1(88, "X2 CrazyLight Medium Blue Archive Edition"),
        Xs1(89, "X2 CrazyLight Medium Blue Archive Edition"),
        Xs1(90, "X2 CrazyLight Medium Blue Archive Edition"),
        Paw3950(91, "eS FS1 Medium"),
        Paw3950(92, "BUZZ-X"),
        Paw3950(93, "STA-X"),
        Paw3950(94, "CARPE-X"),
        Xs1(95, "X2 CrazyLight Pokemon Mew-Two FF Edtion (mini)"),
        Xs1(96, "X2 Crazylight  PRX Pacific Gold Edition (mini)"),
        Xs1(97, "X2H Crazylight  BadseedTech Edition (mini)"),
        Xs1(98, "X2H Crazylight  BadseedTech Edition (Medium)"),
        Paw3950(99, "JINGGG-X"),
        Paw3950(100, "ZywOo The Chosen Mouse Gen.2"),
        Paw3955(101, "Pulsar Feinmann F01 Noctua Edition"),
        Xs1(102, "X3 CrazyLight(Mini/Desert)"),
        Xs1(103, "X3 CrazyLight Medium(Desert)"),
        Xs1(104, "X3 LHD CrazyLight Medium(Desert)"),
        Xs1(105, "X2N CrazyLight(Mini/Ocean)"),
        Xs1(106, "X2N CrazyLight Medium(Ocean)"),
        Paw3955(107, "TenZ 2.0 Mini Misty Blue"),
        Paw3955(108, "CrazyLight LA-2 (Jet Black)"),
        Xs1(109, "X2 CrazyLight Medium Forest"),
        Paw3955(110, "TenZ Signature ED 2.0 Mini"),
        Paw3955(111, "X5"),
        Paw3955(112, "Susanto-X 2.0 Large Black"),
        Paw3955(113, "X2F CrazyLight (Image TBD)"),
        Paw3955(114, "Pro series JV-X 2.0 - TBD (Image TBD)"),
        Paw3955(115, "CrazyLight LA-2 (Uyuni White)"),
        Xs1(116, "Xlite CrazyLight Rock (Size: Mini)"),
        Xs1(117, "X2H CrazyLight Volcano (Size: Mini)"),
        Xs1(118, "Xlite CrazyLight Medium Rock (Size: Medium)"),
        Xs1(119, "X2H CrazyLight Medium Volcano (Size: Medium)"),
        Paw3955(120, "Susanto-X 2.0 Large Phantom Jungle"),
        Paw3955(121, "X4 (Image TBD)"),
        Paw3955(122, "TenZ Signature Edition 2.0 Medium"),
        Paw3955(123, "TenZ 2.0 Medium Misty Blue"),
        Paw3955(124, "Susanto-X 2.0 Medium Phantom Jungle"),
        Paw3955(125, "Susanto-X 2.0 Medium Black"),
    ];

    private static readonly IReadOnlyDictionary<byte, CmouseDeviceProfile> ProfilesByMid =
        Profiles.ToDictionary(profile => profile.Mid);

    public static bool TryGet(byte cid, byte mid, out CmouseDeviceProfile profile)
    {
        if (cid == Cid && ProfilesByMid.TryGetValue(mid, out CmouseDeviceProfile? found))
        {
            profile = found;
            return true;
        }

        profile = null!;
        return false;
    }

    private static CmouseDeviceProfile Xs1(
        byte mid,
        string model,
        DeviceSettingsWriteTrust writeTrust = DeviceSettingsWriteTrust.VendorDerived) =>
        new(Cid, mid, model, Legacy17SensorKind.PulsarXs1, writeTrust);

    private static CmouseDeviceProfile Paw3950(byte mid, string model) =>
        new(Cid, mid, model, Legacy17SensorKind.Paw3950);

    private static CmouseDeviceProfile Paw3955(byte mid, string model) =>
        new(Cid, mid, model, Legacy17SensorKind.Paw3955);
}
