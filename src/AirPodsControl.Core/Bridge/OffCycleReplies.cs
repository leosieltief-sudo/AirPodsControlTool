using System.Text.Json;
using AirPodsControl.Core.Protocol;
using AirPodsControl.Core.Services;

namespace AirPodsControl.Core.Bridge;

/// <summary>Parses the daemon's "read" (controls) and "configure" (settings) replies for Enable Off.</summary>
public static class OffCycleReplies
{
    /// <summary>
    /// Control values the AirPods sent in this session: "controls": {"1a": [v1, v2], "34": [...], "16": [...]}.
    /// Only values the AirPods actually sent; anything missing stays null.
    /// </summary>
    public static OffCycleState ParseState(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("controls", out var c) || c.ValueKind != JsonValueKind.Object) return OffCycleState.Unknown;
            byte[]? V(byte id) => c.TryGetProperty(id.ToString("x2"), out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetByte(out _)).Select(x => x.GetByte()).ToArray()
                : null;
            bool? allow = V(ControlId.AllowOffOption) is [var a, ..] ? a switch { 0x01 => true, 0x02 => false, _ => null } : null;
            CycleModes? cycle = V(ControlId.ListeningModeConfigs) is [var m, ..] && (m & ~(byte)CycleModeSet.All) == 0 ? (CycleModes)m : null;
            HoldAssignment? hold = V(ControlId.ClickHoldMode) is [var r, var l, ..] ? new HoldAssignment(r, l) : null;
            return new OffCycleState(allow, cycle, hold);
        }
        catch (JsonException) { return OffCycleState.Unknown; }
    }

    public sealed record SettingReply(SettingOutcome Outcome, byte? Echo, string? Error);

    /// <summary>"settings": {"allow_off": {"outcome": "confirmed|sent|different|send_failed|not_sent", "echo": n}, "cycle": {...}}.
    /// A key that is absent means the command was not part of the request.</summary>
    public static (SettingReply? allowOff, SettingReply? cycle) ParseSettings(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("settings", out var s) || s.ValueKind != JsonValueKind.Object) return (null, null);
            SettingReply? One(string key)
            {
                if (!s.TryGetProperty(key, out var e) || e.ValueKind != JsonValueKind.Object) return null;
                var o = e.TryGetProperty("outcome", out var ov) ? ov.GetString() : null;
                byte? echo = e.TryGetProperty("echo", out var ev) && ev.ValueKind == JsonValueKind.Number && ev.TryGetByte(out var b) ? b : null;
                var err = e.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() : null;
                return new SettingReply(o switch
                {
                    "confirmed" => SettingOutcome.Confirmed,
                    "sent" => SettingOutcome.Sent,
                    "different" => SettingOutcome.DeviceReportedDifferent,
                    "send_failed" => SettingOutcome.SendFailed,
                    _ => SettingOutcome.NotSent,
                }, echo, err);
            }
            return (One("allow_off"), One("cycle"));
        }
        catch (JsonException) { return (null, null); }
    }
}
