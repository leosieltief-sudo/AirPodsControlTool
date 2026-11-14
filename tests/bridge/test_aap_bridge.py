"""Unit tests for bridge/linux/aap_bridge.py (no Bluetooth hardware: a scripted fake channel stands in).
Run: python -m unittest discover -s tests/bridge -v
Packet vectors are the same ones used by tests/AirPodsControl.Tests/ProtocolTests.cs (LibrePods docs)."""
import errno
import io
import json
import os
import sys
import unittest
from contextlib import redirect_stdout

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "bridge", "linux"))
import aap_bridge as ab  # noqa: E402


def h(s):
    return bytes.fromhex(s.replace(" ", ""))


class FakeClock:
    def __init__(self):
        self.t = 0.0

    def __call__(self):
        return self.t


class FakeChannel:
    """Replies are produced by `responder(sent_packet) -> list[bytes]`; queued packets are delivered on recv."""

    def __init__(self, clock, responder=None, initial=None, drop_after=None):
        self.clock = clock
        self.responder = responder or (lambda p: [])
        self.queue = list(initial or [])
        self.sent = []
        self.drop_after = drop_after
        self.closed = False

    def send(self, data):
        self.sent.append(bytes(data))
        if self.drop_after is not None and len(self.sent) > self.drop_after:
            raise BrokenPipeError(errno.EPIPE, "link lost")
        self.queue.extend(self.responder(bytes(data)))

    def recv(self, timeout):
        if self.queue:
            return self.queue.pop(0)
        self.clock.t += timeout  # nothing arrives: time passes
        return None

    def close(self):
        self.closed = True


def echo_controls(p):
    return [p] if len(p) >= 8 and p[4] == 0x09 else []


class PacketTests(unittest.TestCase):
    def test_vectors_match_csharp(self):
        self.assertEqual(ab.pkt_handshake(), h("00 00 04 00 01 00 02 00 00 00 00 00 00 00 00 00"))
        self.assertEqual(ab.pkt_feature_flags(), h("040004004d00d700000000000000"))
        self.assertEqual(ab.pkt_request_notifications(), h("04 00 04 00 0F 00 FF FF FF FF"))
        self.assertEqual(ab.pkt_listening_mode(ab.MODES["off"]), h("04 00 04 00 09 00 0D 01 00 00 00"))
        self.assertEqual(ab.pkt_listening_mode(ab.MODES["anc"]), h("04 00 04 00 09 00 0D 02 00 00 00"))
        self.assertEqual(ab.pkt_listening_mode(ab.MODES["transparency"]), h("04 00 04 00 09 00 0D 03 00 00 00"))
        self.assertEqual(ab.pkt_listening_mode(ab.MODES["adaptive"]), h("04 00 04 00 09 00 0D 04 00 00 00"))
        self.assertEqual(ab.pkt_allow_off(True), h("04 00 04 00 09 00 34 01 00 00 00"))
        self.assertEqual(ab.pkt_mode_cycle(0x07), h("04 00 04 00 09 00 1A 07 00 00 00"))   # Off + ANC + Transparency
        self.assertEqual(ab.pkt_mode_cycle(0x0B), h("04 00 04 00 09 00 1A 0B 00 00 00"))   # LibrePods doc example
        self.assertEqual(ab.pkt_allow_off(False), h("04 00 04 00 09 00 34 02 00 00 00"))
        self.assertEqual(ab.L2CAP_PSM, 0x1001)

    def test_parse_battery_doc_example(self):
        p = ab.parse(h("04 00 04 00 04 00 03 02 01 64 02 01 04 01 63 01 01 08 01 11 02 01"))
        by = {e["component"]: e for e in p["entries"]}
        self.assertEqual(by["right"]["level"], 100)
        self.assertEqual(by["left"]["level"], 99)
        self.assertTrue(by["left"]["charging"])
        self.assertEqual(by["case"]["level"], 17)

    def test_parse_disconnected_and_garbage(self):
        p = ab.parse(h("04 00 04 00 04 00 01 08 01 32 04 01"))
        self.assertIsNone(p["entries"][0]["level"])
        self.assertEqual(ab.parse(h("01 02 03")), {"type": "unknown"})
        self.assertEqual(ab.parse(h("04 00 04 00 04 00 01 07 01 64 02 01")), {"type": "unknown"})

    def test_parse_ear(self):
        p = ab.parse(h("04 00 04 00 06 00 01 02"))
        self.assertEqual((p["primary"], p["secondary"]), ("out_of_ear", "in_case"))


class SessionTests(unittest.TestCase):
    def make(self, responder=None, initial=None, drop_after=None):
        clock = FakeClock()
        ch = FakeChannel(clock, responder, initial, drop_after)
        return ch, ab.Session(ch, clock=clock, settle=0, confirm_timeout=2.5, log=lambda s: None)

    def test_init_sequence(self):
        ch, s = self.make()
        s.start()
        self.assertEqual(ch.sent, [ab.pkt_handshake(), ab.pkt_feature_flags(), ab.pkt_request_notifications()])

    def test_confirmed(self):
        ch, s = self.make(echo_controls, initial=[ab.pkt_listening_mode(3)])
        s.start()
        s.pump(0.5)
        r = s.set_mode(ab.MODES["anc"], allow_off_first=True)
        self.assertEqual(r["outcome"], "confirmed")
        self.assertEqual(r["mode_before"], 3)
        self.assertEqual(s.mode, 2)

    def test_silence_is_no_response(self):
        ch, s = self.make()
        s.start()
        r = s.set_mode(ab.MODES["transparency"], allow_off_first=False)
        self.assertEqual(r["outcome"], "no_response")
        self.assertIsNone(s.mode)

    def test_off_sends_allow_off_first_and_reports_rejection(self):
        def responder(p):
            if p[4] == 0x09 and p[6] == ab.CTRL_ALLOW_OFF:
                return [p]
            if p[4] == 0x09 and p[6] == ab.CTRL_LISTENING_MODE:
                return [ab.pkt_listening_mode(ab.MODES["anc"])]
            return []
        ch, s = self.make(responder)
        s.start()
        r = s.set_mode(ab.MODES["off"], allow_off_first=True)
        self.assertEqual(ch.sent[3], ab.pkt_allow_off(True))
        self.assertEqual(ch.sent[4], ab.pkt_listening_mode(1))
        self.assertEqual(r["outcome"], "different")
        self.assertEqual(r["reported"], 2)
        self.assertTrue(s.allow_off)

    def test_off_skips_allow_off_when_already_allowed(self):
        ch, s = self.make(echo_controls, initial=[ab.pkt_allow_off(True)])
        s.start()
        s.pump(0.1)
        s.set_mode(ab.MODES["off"], allow_off_first=True)
        self.assertNotIn(ab.pkt_allow_off(True), ch.sent)

    def test_unrelated_packets_do_not_confirm(self):
        def responder(p):
            if p[4] == 0x09 and p[6] == ab.CTRL_LISTENING_MODE:
                return [h("04 00 04 00 04 00 01 04 01 50 02 01"), ab.pkt_control(0x28, 1)]
            return []
        ch, s = self.make(responder)
        s.start()
        r = s.set_mode(ab.MODES["anc"], allow_off_first=False)
        self.assertEqual(r["outcome"], "no_response")
        self.assertEqual(s.battery["left"]["level"], 80)

    def test_link_loss_raises(self):
        ch, s = self.make(drop_after=3)
        s.start()
        with self.assertRaises(OSError):
            s.set_mode(ab.MODES["anc"], allow_off_first=False)


class ConnectRetryTests(unittest.TestCase):
    def test_retries_refused_then_succeeds(self):
        calls = []

        def connect(mac, psm, timeout):
            calls.append((mac, psm))
            if len(calls) < 3:
                raise ConnectionRefusedError(errno.ECONNREFUSED, "refused")
            return "channel"
        ab.time_sleep_backup = ab.time.sleep
        try:
            ab.time.sleep = lambda s: None
            ch = ab.connect_with_retry("A1:B2:C3:D4:E5:F6", deadline=1e18, connect=connect, log=lambda s: None)
        finally:
            ab.time.sleep = ab.time_sleep_backup
        self.assertEqual(ch, "channel")
        self.assertEqual(calls[0], ("A1:B2:C3:D4:E5:F6", 0x1001))
        self.assertEqual(len(calls), 3)

    def test_non_retriable_error_is_raised(self):
        def connect(mac, psm, timeout):
            raise PermissionError(errno.EACCES, "denied")
        with self.assertRaises(PermissionError):
            ab.connect_with_retry("AA:BB:CC:DD:EE:FF", deadline=1e18, connect=connect, log=lambda s: None)

    def test_run_reports_connect_failure_as_result_json(self):
        class Args:
            mac = "AA:BB:CC:DD:EE:FF"; mode = "anc"; allow_off = False
            connect_timeout = 0.0; confirm_timeout = 0.1; settle = 0

        def connect(mac, psm, timeout):
            raise ConnectionRefusedError(errno.ECONNREFUSED, "refused")
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = ab.run(Args, connect=connect)
        line = [l for l in buf.getvalue().splitlines() if l.startswith("RESULT ")][-1]
        res = json.loads(line[len("RESULT "):])
        self.assertEqual(rc, 1)
        self.assertFalse(res["ok"])
        self.assertEqual(res["outcome"], "connect_failed")
        self.assertEqual(res["requested"], 2)

    def test_selftest_json(self):
        buf = io.StringIO()
        with redirect_stdout(buf):
            ab.selftest()
        d = json.loads(buf.getvalue())
        self.assertEqual(d["mode_adaptive"], "0400040009000d04000000")


if __name__ == "__main__":
    unittest.main()


# ---------------------------------------------------------------- LE scan parsing (adapter test)
ADDR = "11:22:33:44:55:66"
ADDR_LE = "665544332211"
# flags + Apple manufacturer data: 4C00 | 07 19 | prefix 01 | model 2720 (AirPods Pro 3, LE 0x2027) | 22 bytes
APPLE_AD = h("020106" + "1EFF4C00" + "0719" + "01" + "2720" + "55" * 22)


def legacy_report(ad, rssi=0xC4):
    body = h("01" + "00" + "01" + ADDR_LE) + bytes([len(ad)]) + ad + bytes([rssi])
    return h("043E") + bytes([len(body) + 1]) + h("02") + body


def extended_report(ad, rssi=0xBA):
    body = (h("01" + "1300" + "01" + ADDR_LE + "01" + "00" + "FF" + "7F") + bytes([rssi])
            + h("0000" + "00" + "000000000000") + bytes([len(ad)]) + ad)
    return h("043E") + bytes([len(body) + 1]) + h("0D") + body


class FakeHciSocket:
    def __init__(self, packets):
        self.packets = list(packets)
        self.closed = False

    def settimeout(self, _):
        pass

    def recv(self, _):
        if self.packets:
            return self.packets.pop(0)
        raise ab.socket.timeout()

    def close(self):
        self.closed = True


class ScanTests(unittest.TestCase):
    def test_proximity_model_little_endian(self):
        self.assertEqual(ab.proximity_model(APPLE_AD), 0x2027)

    def test_non_apple_and_other_apple_types_ignored(self):
        self.assertIsNone(ab.proximity_model(h("020106" + "05FF75000102")))      # Samsung company id
        self.assertIsNone(ab.proximity_model(h("0AFF4C001005031C000000")))       # Apple "nearby" (0x10), not 0x07
        self.assertIsNone(ab.proximity_model(h("FF")))                           # truncated

    def test_legacy_report(self):
        [(addr, rssi, data)] = ab.parse_le_reports(legacy_report(APPLE_AD))
        self.assertEqual((addr, rssi, data), (ADDR, -60, APPLE_AD))

    def test_extended_report(self):
        [(addr, rssi, data)] = ab.parse_le_reports(extended_report(APPLE_AD))
        self.assertEqual((addr, rssi, data), (ADDR, -70, APPLE_AD))

    def test_non_le_event_ignored(self):
        self.assertEqual(ab.parse_le_reports(h("040E0401030C00")), [])

    def test_scan_reports_airpods(self):
        sock = FakeHciSocket([legacy_report(APPLE_AD), extended_report(APPLE_AD), legacy_report(h("020106"))])
        out = io.StringIO()
        with redirect_stdout(out):
            rc = ab.scan(0.3, sock=sock)
        self.assertEqual(rc, 0)
        self.assertTrue(sock.closed)
        line = [l for l in out.getvalue().splitlines() if l.startswith("SCAN ")][-1]
        r = json.loads(line[5:])
        self.assertEqual(r["advertisers"], 1)
        self.assertEqual(r["apple_proximity"], [{"address": ADDR, "model": "0x2027", "rssi": -60, "count": 2}])


class HciFilterTests(unittest.TestCase):
    def test_filter_has_kernel_struct_size(self):
        # sizeof(struct hci_ufilter) == 16 on Linux (EINVAL for shorter buffers, seen on the WSL 6.6 kernel)
        self.assertEqual(len(ab.HCI_FILTER_EVENTS), 16)


class A2dpSessionBehaviourTests(unittest.TestCase):
    """Behaviour seen live on AirPods Pro 3 (14.11.2026) once an A2DP connection exists."""

    def make(self, responder=None, initial=None, state_timeout=5.0):
        clock = FakeClock()
        ch = FakeChannel(clock, responder, initial)
        return ch, ab.Session(ch, clock=clock, settle=0, confirm_timeout=4.0, log=lambda s: None, state_timeout=state_timeout)

    def test_waits_for_reported_state_before_commanding(self):
        # the state burst only follows the notification request
        def responder(p):
            if p == ab.pkt_request_notifications():
                return [ab.pkt_listening_mode(3)]
            return echo_controls(p)
        ch, s = self.make(responder)
        s.start()
        self.assertEqual(s.mode, 3)
        r = s.set_mode(ab.MODES["anc"], allow_off_first=False)
        self.assertEqual(r["outcome"], "confirmed")
        self.assertEqual(r["mode_before"], 3)

    def test_already_set_is_confirmed_without_echo(self):
        # AirPods send no echo for the mode they are already in
        ch, s = self.make(lambda p: [], initial=[ab.pkt_listening_mode(2)])
        s.start()
        r = s.set_mode(ab.MODES["anc"], allow_off_first=False)
        self.assertEqual(r["outcome"], "confirmed")
        self.assertTrue(r["already_set"])
        self.assertNotIn(ab.pkt_listening_mode(2), ch.sent)

    def test_late_duplicate_of_old_state_is_not_taken_as_reply(self):
        def responder(p):
            if p == ab.pkt_listening_mode(2):
                return [ab.pkt_listening_mode(3), p]   # stale old state first, then the real echo
            return []
        ch, s = self.make(responder, initial=[ab.pkt_listening_mode(3)])
        s.start()
        r = s.set_mode(ab.MODES["anc"], allow_off_first=False)
        self.assertEqual(r["outcome"], "confirmed")

    def test_only_old_state_reported_is_different(self):
        def responder(p):
            return [ab.pkt_listening_mode(3)] if p == ab.pkt_listening_mode(2) else []
        ch, s = self.make(responder, initial=[ab.pkt_listening_mode(3)])
        s.start()
        r = s.set_mode(ab.MODES["anc"], allow_off_first=False)
        self.assertEqual((r["outcome"], r["reported"]), ("different", 3))

    def test_no_state_still_tries_and_reports_no_response(self):
        ch, s = self.make(lambda p: [], state_timeout=1.0)
        s.start()
        r = s.set_mode(ab.MODES["anc"], allow_off_first=False)
        self.assertEqual(r["outcome"], "no_response")
        self.assertIn(ab.pkt_listening_mode(2), ch.sent)

class SentOutcomeTests(unittest.TestCase):
    """AirPods Pro 3 without A2DP: handshake answered, command applied, no echo (verified by ear, 14.11.2026)."""

    def make(self, responder):
        clock = FakeClock()
        ch = FakeChannel(clock, responder)
        return ch, ab.Session(ch, clock=clock, settle=0, confirm_timeout=1.5, log=lambda s: None)

    def test_acknowledged_channel_without_echo_is_sent(self):
        def responder(p):
            return [h("010004000000010003000000000000000000")] if p == ab.pkt_handshake() else []
        ch, s = self.make(responder)
        s.start()
        self.assertTrue(s.handshake_ok)
        r = s.set_mode(ab.MODES["transparency"], allow_off_first=False)
        self.assertEqual(r["outcome"], "sent")
        self.assertIn(ab.pkt_listening_mode(3), ch.sent)

    def test_no_handshake_reply_stays_no_response(self):
        ch, s = self.make(lambda p: [])
        s.start()
        self.assertFalse(s.handshake_ok)
        self.assertEqual(s.set_mode(ab.MODES["anc"], allow_off_first=False)["outcome"], "no_response")