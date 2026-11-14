"""Unit tests for bridge/linux/aapd.py (persistent daemon). No Bluetooth hardware: fake management socket and channel.
Run: python -m unittest discover -s tests/bridge -v"""
import os
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "bridge", "linux"))
sys.path.insert(0, os.path.dirname(__file__))
import aapd  # noqa: E402
from test_aap_bridge import FakeChannel, FakeClock, echo_controls, h  # noqa: E402

ADAPTER = "AA:BB:CC:00:11:22"
AIRPODS = "A1:B2:C3:D4:E5:F6"
KEY_HEX = "00112233445566778899AABBCCDDEEFF"
HANDSHAKE_ACK = h("01 00 04 00 00 00")
BATTERY = h("04 00 04 00 04 00 02 04 01 44 02 01 02 01 43 02 01")


def write_key(root, adapter=ADAPTER, device=AIRPODS, key=KEY_HEX, key_type=4):
    d = os.path.join(root, adapter, device)
    os.makedirs(d)
    with open(os.path.join(d, "info"), "w", encoding="utf-8") as f:
        f.write("[General]\nName=AirPods\n\n[LinkKey]\nKey=%s\nType=%d\nPINLength=0\n" % (key, key_type))


class FakeMgmt:
    fd = -1

    def __init__(self, clock, indexes=(0,), powered_after=1, fail_power=False):
        self.clock = clock
        self.indexes = list(indexes)
        self.powered_after = powered_after
        self.fail_power = fail_power
        self.power_calls = 0
        self.loaded = None
        self.settings_set = []
        self.disconnected = []

    def index_list(self):
        return list(self.indexes)

    def read_event(self, timeout):
        self.clock.t += timeout
        return None

    def read_info(self, index):
        powered = not self.fail_power and self.power_calls >= self.powered_after
        return {"address": ADAPTER, "settings": 1 if powered else 0}

    def set_setting(self, opcode, index, value, timeout=5.0):
        if opcode == aapd.OP_SET_POWERED:
            self.power_calls += 1
            if self.fail_power:
                raise aapd.MgmtError(opcode, 0x03)
            return 1 if self.power_calls >= self.powered_after else 0
        self.settings_set.append(opcode)
        return 0

    def load_link_keys(self, index, keys):
        self.loaded = keys

    def disconnect(self, index, mac):
        self.disconnected.append(mac)


def responder(p):
    if p[:4] == h("00 00 04 00"):
        return [HANDSHAKE_ACK]
    if p[4] == 0x0F:
        return [BATTERY]
    return []


class Harness:
    def __init__(self, test, **mgmt_kw):
        self.clock = FakeClock()
        self.keys = {}          # adapter -> [(device, type, key, pin_len)] (BlueZ storage paths contain ':', not valid on Windows)
        self.mgmt = FakeMgmt(self.clock, **mgmt_kw)
        self.out = []
        self.channels = []
        self.runs = []

        def connect(mac, psm, timeout):
            ch = FakeChannel(self.clock, responder)
            ch.fd = 100 + len(self.channels)
            self.channels.append(ch)
            return ch

        def run(cmd, **kw):
            self.runs.append(cmd)
            if "bluez-up" in cmd:
                return type("R", (), {"stdout": 'RESULT {"ok": true, "outcome": "bluez_up"}\n'})()
            return type("R", (), {"stdout": 'RESULT {"ok": true}\n'})()

        self.daemon = aapd.Daemon(mgmt_factory=lambda: self.mgmt, connect=connect, out=self.out.append,
                                  clock=self.clock, run=run, key_loader=lambda a: self.keys.get(a, []), sleep=lambda s: None)

    def switch(self, mode="anc", **kw):
        req = {"id": 1, "op": "switch", "mac": AIRPODS, "mode": mode, "hci_timeout": 2}
        req.update(kw)
        return self.daemon.op_switch(req)


class KeyStorageTests(unittest.TestCase):
    def test_link_key_packet_layout(self):
        key = bytes(range(16))
        p = aapd.pack_link_keys([(AIRPODS, 4, key, 0)])
        self.assertEqual(p[:3], b"\x00\x01\x00")                                  # debug_keys=0, count=1
        self.assertEqual(p[3:9], bytes.fromhex("F6E5D4C3B2A1"))                   # bdaddr little-endian
        self.assertEqual(p[9:11], b"\x00\x04")                                    # BR/EDR, type 4
        self.assertEqual(p[11:27], key)
        self.assertEqual(p[27:], b"\x00")
        self.assertEqual(len(p), 3 + 25)

    def test_info_file_parsing_keeps_byte_order(self):
        parsed = aapd.parse_info_file("[General]\nName=x\n[LinkKey]\nKey=%s\nType=5\nPINLength=0\n" % KEY_HEX)
        self.assertEqual(parsed, (bytes.fromhex(KEY_HEX), 5, 0))
        self.assertIsNone(aapd.parse_info_file("[General]\nName=x\n"))

    @unittest.skipIf(os.name == "nt", "BlueZ storage directory names contain ':' (Linux only)")
    def test_loads_only_this_adapters_keys(self):
        root = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(root, ignore_errors=True))
        write_key(root)
        write_key(root, adapter="11:11:11:11:11:11", device="22:22:22:22:22:22")
        keys = aapd.load_stored_keys(ADAPTER, root)
        self.assertEqual(keys, [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)])
        self.assertEqual(aapd.load_stored_keys("99:99:99:99:99:99", root), [])

    def test_sockaddr_and_mgmt_header_sizes(self):
        self.assertEqual(len(struct.pack("<HHH", aapd.AF_BLUETOOTH, aapd.INDEX_NONE, aapd.HCI_CHANNEL_CONTROL)), 6)


class SwitchTests(unittest.TestCase):
    def test_switch_without_bluetoothd_sends_mode_and_reports_sent(self):
        hs = Harness(self)
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]
        r = hs.switch("transparency")
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["outcome"], "sent")
        self.assertEqual(r["stack"], "mgmt")
        self.assertEqual(hs.mgmt.loaded, [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)])
        for stage in ("hci", "keys", "powered", "connected", "handshake", "sent", "total"):
            self.assertIn(stage, r["t"])
        sent = hs.channels[0].sent
        self.assertEqual(sent[-1], h("04 00 04 00 09 00 0D 03 00 00 00"))
        self.assertEqual([b["component"] for b in r["battery"]], ["left", "right"])
        self.assertEqual(hs.runs, [["modprobe", "btusb"]])            # no bluetoothd
        self.assertTrue(any(o.get("type") == "stage" and o.get("stage") == "powered" for o in hs.out))

    def test_power_on_is_retried(self):
        hs = Harness(self, powered_after=3)
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]
        r = hs.switch()
        self.assertTrue(r["ok"], r)
        self.assertGreaterEqual(r["t"]["power_attempts"], 2)

    def test_second_switch_reuses_open_channel(self):
        hs = Harness(self)
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]
        hs.switch("anc")
        r = hs.switch("transparency")
        self.assertTrue(r["ok"], r)
        self.assertTrue(r["t"].get("channel_reused"))
        self.assertEqual(len(hs.channels), 1)
        self.assertEqual(hs.channels[0].sent[-1], h("04 00 04 00 09 00 0D 03 00 00 00"))

    def test_dead_reused_channel_is_reopened_once(self):
        hs = Harness(self)
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]
        hs.switch("anc")
        hs.channels[0].drop_after = 0           # every further send fails
        r = hs.switch("off", allow_off=False)
        self.assertTrue(r["ok"], r)
        self.assertTrue(r["t"].get("channel_reopened"))
        self.assertEqual(len(hs.channels), 2)

    def test_missing_key_is_reported(self):
        hs = Harness(self)
        r = hs.switch()
        self.assertFalse(r["ok"])
        self.assertEqual(r["outcome"], "no_key")
        self.assertEqual(hs.channels, [])

    def test_missing_adapter_times_out(self):
        hs = Harness(self, indexes=())
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]
        r = hs.switch()
        self.assertEqual(r["outcome"], "adapter_not_ready")

    def test_falls_back_to_bluetoothd_when_mgmt_power_fails(self):
        hs = Harness(self, fail_power=True)
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]
        r = hs.switch()
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["stack"], "bluez")
        self.assertIn("mgmt_error", r["t"])
        self.assertTrue(any("bluez-up" in c for c in hs.runs))

    def test_bad_arguments(self):
        hs = Harness(self)
        r = hs.daemon.op_switch({"id": 1, "op": "switch", "mac": AIRPODS, "mode": "loud"})
        self.assertEqual(r["outcome"], "bad_arguments")


class ReleaseAndProtocolTests(unittest.TestCase):
    def test_release_closes_channel_and_drops_link(self):
        hs = Harness(self)
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]
        hs.switch()
        r = hs.daemon.op_release({})
        self.assertTrue(r["acl_dropped"])
        self.assertTrue(hs.channels[0].closed)
        self.assertEqual(hs.mgmt.disconnected, [AIRPODS])
        self.assertIsNone(hs.daemon.index)

    def test_release_after_bluez_fallback_stops_bluetoothd(self):
        hs = Harness(self, fail_power=True)
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]
        hs.switch()
        hs.daemon.op_release({})
        self.assertTrue(any("bluez-down" in c for c in hs.runs))

    def test_handle_replies_with_request_id(self):
        hs = Harness(self)
        self.assertTrue(hs.daemon.handle({"id": 42, "op": "ping"}))
        reply = hs.out[-1]
        self.assertEqual((reply["type"], reply["id"], reply["outcome"]), ("reply", 42, "pong"))
        self.assertFalse(hs.daemon.handle({"id": 43, "op": "quit"}))
        self.assertEqual(hs.out[-1]["outcome"], "bye")

    def test_unknown_op(self):
        hs = Harness(self)
        hs.daemon.handle({"id": 7, "op": "dance"})
        self.assertEqual(hs.out[-1]["outcome"], "bad_arguments")


class TraceTests(unittest.TestCase):
    def test_kernel_events_are_decoded_with_reason(self):
        addr = bytes.fromhex("F6E5D4C3B2A1")
        self.assertEqual(aapd.decode_mgmt_event(0x000B, 0, addr + b"\x00" + b"\x00" * 6),
                         ("acl_connected", "addr=%s type=0" % AIRPODS))
        self.assertEqual(aapd.decode_mgmt_event(0x000C, 0, addr + b"\x00\x02"),
                         ("acl_disconnected", "addr=%s reason=local_host_terminated" % AIRPODS))
        self.assertEqual(aapd.decode_mgmt_event(0x000C, 0, addr + b"\x00\x03")[1].split("reason=")[1], "remote_terminated")
        self.assertEqual(aapd.decode_mgmt_event(0x0005, 1, b""), ("index_removed", "hci1"))
        self.assertEqual(aapd.decode_mgmt_event(0x0012, 0, b"\x00" * 20), (None, None))

    def test_events_of_an_operation_are_returned_relative_to_the_request(self):
        hs = Harness(self)
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]
        mon = aapd.EventMonitor(clock=hs.clock, start=False)
        hs.daemon.monitor = mon
        hs.daemon.handle({"id": 1, "op": "switch", "op_id": "op1-x", "mac": AIRPODS, "mode": "anc", "hci_timeout": 2})
        mon.add(hs.clock.t, "acl_connected", "addr=" + AIRPODS)
        hs.clock.t += 1.0
        mon.add(hs.clock.t, "acl_disconnected", "addr=%s reason=local_host_terminated" % AIRPODS)
        hs.clock.t += 0.5
        hs.daemon.handle({"id": 2, "op": "events", "op_id": "op1-x"})
        r = hs.out[-1]
        self.assertEqual(r["op_id"], "op1-x")
        self.assertEqual([e[1] for e in r["events"]], ["acl_connected", "acl_disconnected"])
        self.assertTrue(all(e[0] <= 0 for e in r["events"]))         # both before the events request

    def test_switch_reply_lists_received_packets(self):
        hs = Harness(self)
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]
        r = hs.switch()
        self.assertIn("handshake_ack", [d for _, d in r["rx"]])
        self.assertIn("battery", [d for _, d in r["rx"]])
        self.assertFalse(any(d.startswith("control id=0x0D") for _, d in r["rx"]))   # no echo in this fake: outcome stays "sent"
        self.assertEqual(r["outcome"], "sent")


class ReadTests(unittest.TestCase):
    def test_read_collects_battery_without_sending_a_command(self):
        hs = Harness(self)
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]
        r = hs.daemon.op_read({"id": 1, "op": "read", "mac": AIRPODS, "hci_timeout": 2, "listen": 0.5})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["outcome"], "read_only")
        self.assertEqual(len(r["battery"]), 2)
        self.assertFalse(any(p[4] == 0x09 for p in hs.channels[0].sent if len(p) > 4))   # no control packet at all

    def test_switch_after_read_reuses_the_channel(self):
        hs = Harness(self)
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]
        hs.daemon.op_read({"id": 1, "op": "read", "mac": AIRPODS, "hci_timeout": 2, "listen": 0.1})
        r = hs.switch("transparency")
        self.assertTrue(r["t"].get("channel_reused"))
        self.assertEqual(len(hs.channels), 1)

    def test_read_without_handshake_is_no_response(self):
        hs = Harness(self)
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]
        hs.daemon.connect = lambda mac, psm, timeout: FakeChannel(hs.clock)    # silent AirPods
        r = hs.daemon.op_read({"id": 1, "op": "read", "mac": AIRPODS, "hci_timeout": 2, "listen": 0.1})
        self.assertFalse(r["ok"])
        self.assertEqual(r["outcome"], "no_response")


def controls_of(ch):
    """Control packets (id, d1) the daemon wrote, in order."""
    return [(p[6], p[7]) for p in ch.sent if len(p) >= 8 and p[4] == 0x09]


class HoldCycleTests(unittest.TestCase):
    """Press-and-hold cycle: read (state) then configure (0x34 + 0x1A) on one channel, nothing repeated, never 0x0D / 0x16."""

    def harness(self, reply=responder, drop_after=None):
        hs = Harness(self)
        hs.keys[ADAPTER] = [(AIRPODS, 4, bytes.fromhex(KEY_HEX), 0)]

        def connect(mac, psm, timeout):
            ch = FakeChannel(hs.clock, reply, drop_after=drop_after)
            ch.fd = 200 + len(hs.channels)
            hs.channels.append(ch)
            return ch
        hs.daemon.connect = connect
        return hs

    def read(self, hs, listen=3.0):
        return hs.daemon.op_read({"id": 1, "op": "read", "mac": AIRPODS, "hci_timeout": 2, "listen": listen,
                                  "wait_controls": [0x1A, 0x34, 0x16]})

    def configure(self, hs, **kw):
        req = {"id": 2, "op": "configure", "mac": AIRPODS, "allow_off": True, "cycle": 0x07, "confirm_timeout": 1.0}
        req.update(kw)
        return hs.daemon.op_configure(req)

    def test_silent_airpods_give_sent_not_confirmed(self):
        hs = self.harness()                    # Pro 3 without audio: handshake + battery, no 0x09 at all
        r0 = self.read(hs)
        self.assertEqual(r0["controls"], {})   # nothing reported: the Windows side must not assume a current set
        r = self.configure(hs)
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["outcome"], "configured")
        self.assertEqual(r["settings"]["allow_off"]["outcome"], "sent")
        self.assertEqual(r["settings"]["cycle"]["outcome"], "sent")
        self.assertEqual(controls_of(hs.channels[0]), [(0x34, 0x01), (0x1A, 0x07)])   # once each, allow-off first
        self.assertEqual(len(hs.channels), 1)  # same AAP session as the read

    def test_echo_is_confirmed(self):
        def reply(p):
            return responder(p) + echo_controls(p)
        hs = self.harness(reply)
        self.read(hs, listen=0.2)
        r = self.configure(hs, cycle=0x0F)
        self.assertEqual(r["settings"]["allow_off"], {"outcome": "confirmed", "echo": 1})
        self.assertEqual(r["settings"]["cycle"], {"outcome": "confirmed", "echo": 0x0F})

    def test_read_reports_cycle_hold_and_allow_off_and_stops_early(self):
        state = [h("04 00 04 00 09 00 1A 06 00 00 00"), h("04 00 04 00 09 00 34 02 00 00 00"), h("04 00 04 00 09 00 16 01 05 00 00")]

        def reply(p):
            return responder(p) + (state if p[4] == 0x0F else [])
        hs = self.harness(reply)
        r = self.read(hs, listen=3.0)
        self.assertEqual(r["controls"], {"16": [0x01, 0x05], "1a": [0x06, 0x00], "34": [0x02, 0x00]})
        self.assertLess(r["t"]["listened"] - r["t"]["wait_controls"], 0.5)   # stopped early once all three were reported
        self.assertFalse(controls_of(hs.channels[0]))

    def test_refused_allow_off_does_not_send_the_cycle(self):
        def reply(p):
            if len(p) >= 8 and p[4] == 0x09 and p[6] == 0x34:
                return [h("04 00 04 00 09 00 34 02 00 00 00")]
            return responder(p)
        hs = self.harness(reply)
        self.read(hs, listen=0.2)
        r = self.configure(hs)
        self.assertEqual(r["settings"]["allow_off"], {"outcome": "different", "echo": 2})
        self.assertEqual(r["settings"]["cycle"]["outcome"], "not_sent")
        self.assertEqual(controls_of(hs.channels[0]), [(0x34, 0x01)])

    def test_write_failure_is_reported_and_not_retried(self):
        hs = self.harness(drop_after=3)       # handshake, 0x4D, 0x0F go out; the 4th write (0x34) fails
        self.read(hs, listen=0.2)
        r = self.configure(hs)
        self.assertFalse(r["ok"])
        self.assertEqual(r["outcome"], "transport_error")
        self.assertEqual(r["settings"]["allow_off"]["outcome"], "send_failed")
        self.assertEqual(r["settings"]["cycle"]["outcome"], "not_sent")
        self.assertEqual(len(hs.channels), 1)  # no reconnect, no second attempt
        self.assertEqual(sum(1 for p in hs.channels[0].sent if len(p) > 6 and p[4] == 0x09), 1)

    def test_cycle_only_when_off_is_already_allowed(self):
        hs = self.harness()
        self.read(hs, listen=0.2)
        r = self.configure(hs, allow_off=False, cycle=0x0B)
        self.assertNotIn("allow_off", r["settings"])
        self.assertEqual(controls_of(hs.channels[0]), [(0x1A, 0x0B)])

    def test_invalid_masks_are_rejected_before_writing(self):
        hs = self.harness()
        self.read(hs, listen=0.2)
        for bad in (0x00, 0x01, 0x02, 0x10, 0x1F, -1, "7"):
            r = self.configure(hs, cycle=bad)
            self.assertEqual(r["outcome"], "bad_arguments", bad)
        self.assertEqual(controls_of(hs.channels[0]), [])

    def test_configure_without_a_read_channel_does_not_connect(self):
        hs = self.harness()
        r = self.configure(hs)
        self.assertEqual(r["outcome"], "no_channel")
        self.assertEqual(hs.channels, [])

    def test_never_sends_listening_mode_or_hold_action(self):
        def reply(p):
            return responder(p) + echo_controls(p)
        hs = self.harness(reply)
        self.read(hs, listen=0.2)
        self.configure(hs, cycle=0x0F)
        ids = [i for i, _ in controls_of(hs.channels[0])]
        self.assertNotIn(0x0D, ids)
        self.assertNotIn(0x16, ids)

    def test_handle_routes_configure_and_echoes_op_id(self):
        hs = self.harness()
        hs.daemon.handle({"id": 1, "op": "read", "mac": AIRPODS, "hci_timeout": 2, "listen": 0.2, "op_id": "op1-ab"})
        hs.daemon.handle({"id": 2, "op": "configure", "mac": AIRPODS, "allow_off": True, "cycle": 7, "op_id": "op1-ab"})
        reply = [o for o in hs.out if o.get("type") == "reply"][-1]
        self.assertEqual(reply["outcome"], "configured")
        self.assertEqual(reply["op_id"], "op1-ab")


if __name__ == "__main__":
    unittest.main()
