using AirPodsControl.Core.Protocol;

namespace AirPodsControl.Core.Services;

/// <summary>
/// BLE proximity advertisements use rotating random addresses, so they cannot be matched to the paired
/// device by MAC. Like AirPodsDesktop, we accept an advertisement only if its model id equals the paired
/// device's product id and the signal is strong enough to be "the AirPods next to this PC".
/// If several matching devices are nearby, the strongest one within a short window wins.
/// </summary>
public sealed class AdvertisementFilter
{
    private readonly Func<DateTimeOffset> _now;
    private short _bestRssi = short.MinValue;
    private DateTimeOffset _bestAt;

    public AdvertisementFilter(Func<DateTimeOffset>? clock = null) => _now = clock ?? (() => DateTimeOffset.Now);

    public ushort? ExpectedModelId { get; set; }
    public short MinRssi { get; set; } = -72;
    public TimeSpan StrongestWindow { get; set; } = TimeSpan.FromSeconds(10);

    public bool Accept(ProximityPairingMessage msg, short rssi)
    {
        if (ExpectedModelId is not { } expected || msg.ModelId != expected) return false;
        if (rssi < MinRssi) return false;
        var now = _now();
        // Reject a weaker device while a stronger one has been seen recently (allow 8 dB of fluctuation).
        if (now - _bestAt < StrongestWindow && rssi < _bestRssi - 8) return false;
        if (rssi >= _bestRssi || now - _bestAt >= StrongestWindow) { _bestRssi = rssi; _bestAt = now; }
        return true;
    }

    public void Reset() { _bestRssi = short.MinValue; _bestAt = default; }
}
