using AirPodsControl.Core.Protocol;

namespace AirPodsControl.Core.Services;

public enum BatterySource { None, Aap, Ble }

/// <param name="Stale">The value is the last one received, but older than its source's validity window (or the radio
/// went away since): shown with its time, never silently as current.</param>
public sealed record ComponentBattery(int? Level, bool Charging, BatterySource Source, DateTimeOffset? UpdatedAt, bool Stale = false)
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
/// AAP data wins while fresh. When no source is fresh, the newest value received is kept and marked
/// <see cref="ComponentBattery.Stale"/> with its time (never replaced by 0, never presented as current).
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

    private DateTimeOffset? _staleBefore;

    /// <summary>Time of the last accepted BLE advertisement (any component), or null.</summary>
    public DateTimeOffset? LastBleAt { get; private set; }
    /// <summary>Time of the last exact AAP battery report, or null.</summary>
    public DateTimeOffset? LastAapAt { get; private set; }

    /// <summary>Forget everything (another device was selected).</summary>
    public void Reset()
    {
        lock (_gate) { _aL = _aR = _aC = _bL = _bR = _bC = ComponentBattery.Unknown; _staleBefore = null; LastBleAt = LastAapAt = null; }
        Changed?.Invoke(Current);
    }

    /// <summary>The radio went away (Bluetooth off): keep the values, but everything received so far is stale now.</summary>
    public void MarkAllStale()
    {
        lock (_gate) _staleBefore = _now();
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
            if (msg.Entries.Count > 0) LastAapAt = t;
        }
        Changed?.Invoke(Current);
    }

    /// <summary>Exact (1 %) values obtained over AAP outside a persistent channel, e.g. by the WSL bridge session.</summary>
    public void OnAapEntries(IEnumerable<BatteryEntry> entries) => OnAapBattery(new BatteryMessage([], entries.ToList()));

    public void OnBleAdvertisement(ProximityPairingMessage msg)
    {
        var t = _now();
        lock (_gate)
        {
            // An advertisement that lacks a value (e.g. case lid closed, pod out of case) must not erase a fresh value.
            if (msg.LeftPercent is not null) _bL = new(msg.LeftPercent, msg.LeftCharging, BatterySource.Ble, t);
            if (msg.RightPercent is not null) _bR = new(msg.RightPercent, msg.RightCharging, BatterySource.Ble, t);
            if (msg.CasePercent is not null) _bC = new(msg.CasePercent, msg.CaseCharging, BatterySource.Ble, t);
            LastBleAt = t;
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

    /// <summary>Call periodically so values that aged out are re-marked as stale.</summary>
    public void Tick() => Changed?.Invoke(Current);

    private bool Fresh(ComponentBattery c, TimeSpan validity, DateTimeOffset now) =>
        c.IsKnown && c.UpdatedAt is { } t && now - t <= validity && (_staleBefore is not { } sb || t > sb);

    private ComponentBattery Pick(ComponentBattery aap, ComponentBattery ble)
    {
        var now = _now();
        if (Fresh(aap, AapValidity, now)) return aap;
        if (Fresh(ble, BleValidity, now)) return ble;
        // nothing fresh: the newest value we ever got, flagged as stale (AAP wins a tie: it is exact)
        var newest = (aap.IsKnown, ble.IsKnown) switch
        {
            (true, true) => ble.UpdatedAt > aap.UpdatedAt ? ble : aap,
            (true, false) => aap,
            (false, true) => ble,
            _ => ComponentBattery.Unknown,
        };
        return newest.IsKnown ? newest with { Stale = true } : newest;
    }
}
