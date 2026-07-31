using System.Collections.Generic;

namespace PulsarBattery.Device;

/// <summary>
/// The list of supported devices, in probe order. Adding a mouse that speaks
/// an already-implemented protocol is one entry here.
/// </summary>
public static class DeviceRegistry
{
    // Pulsar's shared "8K Dongle" accessory (0x5403) is also used by the X3
    // family; the first Sonix descriptor that matches it wins.
    public static IReadOnlyList<IHidBackend> CreateBackends() =>
    [
        new X2ClBackend(),
        new X2V1Backend(),
        new Sonix64Backend(new DeviceDescriptor("X2 V3 eS", VendorId: 0x3710, ProductIds: [0x3406, 0x5403])),
    ];
}
