using System.Collections.Generic;

namespace PulsarBattery.Device;

/// <summary>
/// Identity of a supported device: its VID/PIDs and display name. Protocol behaviour lives in the
/// paired backend, so a same-protocol mouse is a new descriptor, not a new backend class.
/// </summary>
/// <param name="DongleProductIds">
/// The subset of ProductIds identifying the receiver. Which device answered is ground truth for
/// wired-vs-dongle: the mouse's connection register reads "Dongle" even while genuinely on a cable.
/// </param>
public sealed record DeviceDescriptor(
    string Model,
    int VendorId,
    IReadOnlyList<int> ProductIds,
    IReadOnlyList<int> DongleProductIds);
