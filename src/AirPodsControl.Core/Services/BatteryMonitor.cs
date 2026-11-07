using AirPodsControl.Core.Protocol;

namespace AirPodsControl.Core.Services;

public enum BatterySource { None, Aap, Ble }

public sealed record ComponentBattery(int? Level, bool Charging, BatterySource Source, DateTimeOffset? UpdatedAt)
{
    public static readonly ComponentBattery Unknown = new(null, false, BatterySource.None, null);
    public bool IsKnown => Level is not null;
}

public sealed record BatterySnapshot(ComponentBattery Left, ComponentBattery Right, ComponentBattery Case)
{
    public static readonly BatterySnapshot Unknown = new(ComponentBattery.Unknown, ComponentBattery.Unknown, ComponentBattery.Unknown);
}

/// <summary>
/// Merges battery data from two real sources:
///  * AAP opcode 0x04 (1 % precision, needs the L2CAP control channel)
///  * BLE proximity advertisement (10 % steps, works from user mode)
/// AAP data wins while fresh. Stale data expires to "unknown" instead of showing an old number forever.
/// </summary>
public sealed class BatteryMonitor
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private ComponentBattery _aL = ComponentBattery.Unknown, _aR = ComponentBattery.Unknown, _aC = ComponentBattery.Unknown;
    private ComponentBattery _bL = ComponentBattery.Unknown, _bR = ComponentBattery.Unknown, _bC = ComponentBattery.Unknown;

    public BatteryMonitor(Func<DateTimeOffset>? clock = null) => _now = clock ?? (() => DateTimeOffset.Now);

    public TimeSpan AapValidity { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan BleValidity { get; set; } = TimeSpan.FromSeconds(90);

    public event Action<BatterySnapshot>? Changed;

    public void Reset()
    {
        lock (_gate) { _aL = _aR = _aC = _bL = _bR = _bC = ComponentBattery.Unknown; }
        Changed?.Invoke(Current);
    }

    public void OnAapBattery(BatteryMessage msg)
    {
        var t = _now();
        lock (_gate)
        {
            foreach (var e in msg.Entries)
            {
                var c = new ComponentBattery(e.Level, e.Level is not null && e.IsCharging, BatterySource.Aap, t);
                switch (e.Component)
                {
                    case BatteryComponent.Left: _aL = c; break;
                    case BatteryComponent.Right: _aR = c; break;
                    case BatteryComponent.Case: _aC = c; break;
                }
            }
        }
        Changed?.Invoke(Current);
    }

    public void OnBleAdvertisement(ProximityPairingMessage msg)
    {
        var t = _now();
        lock (_gate)
        {
            // An advertisement that lacks a value (e.g. case lid closed, pod out of case) must not erase a fresh value.
            if (msg.LeftPercent is not null) _bL = new(msg.LeftPercent, msg.LeftCharging, BatterySource.Ble, t);
            if (msg.RightPercent is not null) _bR = new(msg.RightPercent, msg.RightCharging, BatterySource.Ble, t);
            if (msg.CasePercent is not null) _bC = new(msg.CasePercent, msg.CaseCharging, BatterySource.Ble, t);
        }
        Changed?.Invoke(Current);
    }

    public BatterySnapshot Current
    {
        get
        {
            lock (_gate) return new(Pick(_aL, _bL), Pick(_aR, _bR), Pick(_aC, _bC));
        }
    }

    /// <summary>Call periodically so stale values fall back to unknown.</summary>
    public void Tick() => Changed?.Invoke(Current);

    private ComponentBattery Pick(ComponentBattery aap, ComponentBattery ble)
    {
        var now = _now();
        if (aap.IsKnown && aap.UpdatedAt is { } ta && now - ta <= AapValidity) return aap;
        if (ble.IsKnown && ble.UpdatedAt is { } tb && now - tb <= BleValidity) return ble;
        return ComponentBattery.Unknown;
    }
}
