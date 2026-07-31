using System.Collections.Generic;

namespace PulsarBattery.Device;

/// <summary>
/// Identity of a supported device: which VID/PIDs belong to it and what the
/// UI should call it. Protocol behavior lives in the backend the registry
/// pairs this descriptor with — adding a same-protocol mouse is a new
/// descriptor, not a new backend class.
/// </summary>
public sealed record DeviceDescriptor(
    string Model,
    int VendorId,
    IReadOnlyList<int> ProductIds);
