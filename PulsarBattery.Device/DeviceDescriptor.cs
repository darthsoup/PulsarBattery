using System.Collections.Generic;

namespace PulsarBattery.Device;

/// <summary>
/// Identity of a supported device: which VID/PIDs belong to it and what the
/// UI should call it. Protocol behavior lives in the backend the registry
/// pairs this descriptor with. Adding a same-protocol mouse is a new
/// descriptor, not a new backend class.
/// </summary>
/// <param name="DongleProductIds">
/// The subset of <paramref name="ProductIds"/> that identify the wireless
/// receiver rather than the mouse itself. Which physical device answered is
/// the ground truth for wired-vs-dongle: the mouse's own connection-type
/// register reflects its last-established radio link, not live cable state,
/// and stays "wireless" even while genuinely on a charging cable (verified
/// live on the X2 V3 eS: no dongle enumerated, register still read Dongle).
/// </param>
public sealed record DeviceDescriptor(
    string Model,
    int VendorId,
    IReadOnlyList<int> ProductIds,
    IReadOnlyList<int> DongleProductIds);
