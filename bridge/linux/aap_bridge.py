#!/usr/bin/env python3
"""
AirPods Control - WSL2 bridge client.

Opens an L2CAP channel (PSM 0x1001) to the AirPods, performs the AAP handshake, optionally switches the
listening mode and waits for the AirPods to echo it back. Prints exactly one machine-readable line:

    RESULT {"ok": ..., "outcome": ..., ...}

Progress lines ("STAGE <name>") are printed before it. Packet bytes are identical to
src/AirPodsControl.Core/Protocol/AapPackets.cs (source: LibrePods, see docs/PROTOCOL.md);
tests/bridge/test_aap_bridge.py and the C# test BridgeScriptTests verify that both stay in sync.

No third-party modules; works with Python 3.8+.
"""
import argparse
import ctypes
import ctypes.util
import errno
import json
import os
import select
import socket
import struct
import sys
import time

SCRIPT_VERSION = 1

# ---------------------------------------------------------------- protocol (mirror of AapPackets.cs)
L2CAP_PSM = 0x1001
CTRL_LISTENING_MODE = 0x0D
CTRL_ALLOW_OFF = 0x34
CTRL_CLICK_HOLD = 0x16      # press-and-hold action per stem (data1 right, data2 left); read only, never written
CTRL_MODE_CYCLE = 0x1A      # press-and-hold cycle bitmask: Off 0x01, ANC 0x02, Transparency 0x04, Adaptive 0x08
CYCLE_ALL = 0x0F
MODES = {"off": 0x01, "anc": 0x02, "transparency": 0x03, "adaptive": 0x04}
MODE_NAMES = {v: k for k, v in MODES.items()}


def pkt_handshake():
    return bytes([0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])


def pkt_feature_flags(flags=0xD7):
    # LibrePods' client sends D7; LibrePods' docs (macOS capture) show FF
    return bytes([0x04, 0x00, 0x04, 0x00, 0x4D, 0x00, flags, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])


CTRL_OWNS_CONNECTION = 0x06   # LibrePods control_commands.md: "Owns connection", 01 = own, 00 = doesn't own


def pkt_owns_connection(own=True):
    return pkt_control(CTRL_OWNS_CONNECTION, 0x01 if own else 0x00)


def pkt_request_notifications():
    return bytes([0x04, 0x00, 0x04, 0x00, 0x0F, 0x00, 0xFF, 0xFF, 0xFF, 0xFF])


def pkt_control(identifier, d1, d2=0, d3=0, d4=0):
    return bytes([0x04, 0x00, 0x04, 0x00, 0x09, 0x00, identifier, d1, d2, d3, d4])


def pkt_listening_mode(mode_value):
    return pkt_control(CTRL_LISTENING_MODE, mode_value)


def pkt_allow_off(enabled):
    return pkt_control(CTRL_ALLOW_OFF, 0x01 if enabled else 0x02)


def pkt_mode_cycle(mask):
    return pkt_control(CTRL_MODE_CYCLE, mask)


def valid_cycle(mask):
    """Same rule as CycleModeSet.Validate (C#): only the four documented bits, at least two modes."""
    return isinstance(mask, int) and 0 < mask <= CYCLE_ALL and bin(mask).count("1") >= 2


def parse(data):
    """Returns a dict describing one AAP packet (same layouts as AapParser.cs)."""
    if len(data) < 6 or data[0] != 0x04 or data[1] != 0x00 or data[2] != 0x04 or data[3] != 0x00:
        return {"type": "unknown"}
    opcode = data[4] | (data[5] << 8)
    if opcode == 0x0009 and len(data) >= 8:
        return {"type": "control", "id": data[6], "value": data[7], "value2": data[8] if len(data) >= 9 else 0}
    if opcode == 0x0004 and len(data) >= 7:
        count = data[6]
        if count < 1 or count > 3 or len(data) < 7 + count * 5:
            return {"type": "unknown"}
        names = {0x02: "right", 0x04: "left", 0x08: "case"}
        entries = []
        for i in range(count):
            o = 7 + i * 5
            comp, level, status = data[o], data[o + 2], data[o + 3]
            if comp not in names:
                return {"type": "unknown"}
            valid = level <= 100 and status != 0x04
            entries.append({"component": names[comp], "level": level if valid else None,
                            "charging": valid and status in (0x01, 0x05)})
        return {"type": "battery", "entries": entries}
    if opcode == 0x0006 and len(data) >= 8:
        states = {0x00: "in_ear", 0x01: "out_of_ear", 0x02: "in_case"}
        return {"type": "ear", "primary": states.get(data[6], "unknown"), "secondary": states.get(data[7], "unknown")}
    return {"type": "other", "opcode": opcode}


# ---------------------------------------------------------------- L2CAP socket
AF_BLUETOOTH = 31
BTPROTO_L2CAP = 0
SOL_BLUETOOTH = 274
BT_SECURITY = 4
BT_SECURITY_MEDIUM = 2  # authenticated link key + encryption, like LibrePods (auth=true, encrypt=true)


class _SockaddrL2(ctypes.Structure):
    # struct sockaddr_l2 is packed (__attribute__((packed)) in BlueZ); with only 1-/2-byte fields the
    # "ms" layout is byte-identical. Explicit _layout_ silences Python 3.14's deprecation warning.
    _layout_ = "ms"
    _pack_ = 1
    _fields_ = [("l2_family", ctypes.c_ushort), ("l2_psm", ctypes.c_ushort), ("l2_bdaddr", ctypes.c_ubyte * 6),
                ("l2_cid", ctypes.c_ushort), ("l2_bdaddr_type", ctypes.c_ubyte), ("_pad", ctypes.c_ubyte)]


class L2capChannel:
    """Packet channel over a SOCK_SEQPACKET L2CAP socket. Uses Python's AF_BLUETOOTH support when the
    interpreter was built with it, otherwise raw libc calls via ctypes (Alpine's python3 may lack it)."""

    def __init__(self, fd, keepalive=None):
        self.fd = fd
        self._keep = keepalive

    @staticmethod
    def connect(mac, psm, timeout):
        security = struct.pack("BB", BT_SECURITY_MEDIUM, 0)
        if hasattr(socket, "AF_BLUETOOTH") and hasattr(socket, "BTPROTO_L2CAP"):
            s = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_SEQPACKET, socket.BTPROTO_L2CAP)
            try:
                s.setsockopt(SOL_BLUETOOTH, BT_SECURITY, security)
                s.settimeout(timeout)
                s.connect((mac, psm))
                s.setblocking(True)
                return L2capChannel(s.fileno(), keepalive=s)
            except BaseException:
                s.close()
                raise
        libc = ctypes.CDLL(ctypes.util.find_library("c") or None, use_errno=True)
        fd = libc.socket(AF_BLUETOOTH, socket.SOCK_SEQPACKET, BTPROTO_L2CAP)
        if fd < 0:
            e = ctypes.get_errno()
            raise OSError(e, os.strerror(e))
        try:
            sec = ctypes.create_string_buffer(security)
            if libc.setsockopt(fd, SOL_BLUETOOTH, BT_SECURITY, sec, 2) != 0:
                e = ctypes.get_errno()
                raise OSError(e, "setsockopt(BT_SECURITY): " + os.strerror(e))
            addr = _SockaddrL2()
            addr.l2_family = AF_BLUETOOTH
            addr.l2_psm = psm  # little endian host == htobs on x86/arm64
            addr.l2_bdaddr[:] = list(reversed(bytes.fromhex(mac.replace(":", ""))))
            if libc.connect(fd, ctypes.byref(addr), ctypes.sizeof(addr)) != 0:
                e = ctypes.get_errno()
                raise OSError(e, os.strerror(e))
            return L2capChannel(fd)
        except BaseException:
            os.close(fd)
            raise

    def send(self, data):
        os.write(self.fd, data)

    def recv(self, timeout):
        r, _, _ = select.select([self.fd], [], [], max(0.0, timeout))
        if not r:
            return None
        data = os.read(self.fd, 4096)
        if not data:
            raise ConnectionError("AirPods closed the channel")
        return data

    def close(self):
        try:
            if self._keep is not None:
                self._keep.close()
            else:
                os.close(self.fd)
        except OSError:
            pass


# ---------------------------------------------------------------- session logic (unit-tested with a fake channel)
def emit(line):
    print(line, flush=True)


class Session:
    def __init__(self, channel, clock=time.monotonic, settle=1.0, confirm_timeout=2.5, log=emit, state_timeout=0.0):
        self.ch = channel
        self.state_timeout = state_timeout
        self.clock = clock
        self.settle = settle
        self.confirm_timeout = confirm_timeout
        self.log = log
        self.mode = None
        self.allow_off = None
        self.battery = {}
        self.ear = None
        self.rx_packets = 0
        self.trace = False
        self.handshake_ok = False
        self.feature_flags = 0xD7
        self.take_ownership = False
        self.rx_log = []          # (clock, description) of every received packet, for the operation trace
        self.controls = {}        # control id -> [data1, data2] as last reported by the AirPods in this session

    @staticmethod
    def describe(data, p):
        if p.get("type") == "handshake_ack":
            return "handshake_ack"
        if p["type"] == "control":
            return "control id=0x%02X value=0x%02X" % (p["id"], p["value"])
        if p["type"] in ("battery", "ear"):
            return p["type"]
        if p["type"] == "other":
            return "opcode=0x%04X len=%d" % (p["opcode"], len(data))
        return "unparsed len=%d head=%s" % (len(data), bytes(data[:8]).hex())

    def _rx_note(self, data, p):
        if len(self.rx_log) < 200:
            self.rx_log.append((self.clock(), self.describe(data, p)))

    def _send(self, data):
        if self.trace:
            self.log("PKT tx " + data.hex())
        self.ch.send(data)

    def _handle(self, data):
        self.rx_packets += 1
        if self.trace:
            self.log("PKT rx " + data.hex())
        if data[:4] == b"\x01\x00\x04\x00":   # handshake reply
            self.handshake_ok = True
            p = {"type": "handshake_ack"}
            self._rx_note(data, p)
            return p
        p = parse(data)
        self._rx_note(data, p)
        if p["type"] == "control":
            self.controls[p["id"]] = [p["value"], p["value2"]]
            if p["id"] == CTRL_LISTENING_MODE and p["value"] in MODE_NAMES:
                self.mode = p["value"]
            elif p["id"] == CTRL_ALLOW_OFF:
                self.allow_off = p["value"] == 0x01
        elif p["type"] == "battery":
            for e in p["entries"]:
                self.battery[e["component"]] = e
        elif p["type"] == "ear":
            self.ear = {"primary": p["primary"], "secondary": p["secondary"]}
        return p

    def pump(self, duration, until=None):
        """Reads packets for `duration` seconds or until `until(packet)` is true; returns the matching packet."""
        end = self.clock() + duration
        while True:
            left = end - self.clock()
            if left <= 0:
                return None
            data = self.ch.recv(left)
            if data is None:
                return None
            p = self._handle(data)
            if until is not None and until(p):
                return p

    def start(self):
        self.log("STAGE handshake")
        gap = 0.1 if self.settle else 0.0  # same pacing as AirPodsProtocolService.InitPacketGap
        self._send(pkt_handshake())
        # Wait for the handshake reply before anything else: on AirPods Pro 3 (live test, 14.11.2026) packets sent
        # 0.1 s after the handshake without waiting produced no battery/mode notifications.
        self.pump(1.5, until=lambda p: p["type"] == "handshake_ack")
        time.sleep(gap)
        self._send(pkt_feature_flags(self.feature_flags))
        time.sleep(gap)
        self._send(pkt_request_notifications())
        if self.take_ownership:
            time.sleep(gap)
            self.log("STAGE own")
            self._send(pkt_owns_connection(True))
        self.pump(self.settle)  # collect current mode / battery / ear state
        if self.mode is None and self.state_timeout > 0:
            # With an A2DP connection the AirPods send their control state a few seconds after 0x0F (live test:
            # ~2-3 s). A command sent before that was not confirmed, so wait for the current listening mode first.
            self.log("STAGE wait_state")
            self.pump(self.state_timeout, until=lambda p: p["type"] == "control" and p["id"] == CTRL_LISTENING_MODE)

    def send_control_and_wait(self, identifier, packet):
        self._send(packet)
        return self.pump(self.confirm_timeout, until=lambda p: p["type"] == "control" and p["id"] == identifier)

    def set_mode(self, mode_value, allow_off_first):
        mode_before = self.mode
        if mode_value == MODES["off"] and allow_off_first and self.allow_off is not True:
            self.log("STAGE allow_off")
            echo = self.send_control_and_wait(CTRL_ALLOW_OFF, pkt_allow_off(True))
            if echo is not None:
                self.allow_off = echo["value"] == 0x01
        if self.mode == mode_value:
            # The AirPods do not echo a command for the mode they are already in (live test); their own report
            # from this session already says it is set.
            return {"outcome": "confirmed", "reported": mode_value, "mode_before": mode_before, "already_set": True}
        self.log("STAGE set_mode")
        self._send(pkt_listening_mode(mode_value))
        # a late duplicate of the *old* state may still arrive: wait for the requested value, remember others
        seen = []

        def done(p):
            if p["type"] == "control" and p["id"] == CTRL_LISTENING_MODE:
                seen.append(p["value"])
                return p["value"] == mode_value
            return False
        reply = self.pump(self.confirm_timeout, until=done)
        if reply is not None:
            return {"outcome": "confirmed", "reported": mode_value, "mode_before": mode_before}
        if seen:
            return {"outcome": "different", "reported": seen[-1], "mode_before": mode_before}
        if self.handshake_ok:
            # AirPods Pro 3 apply the command over this channel but send no echo to a host without an audio
            # connection (verified by ear on the test PC, 14.11.2026). Delivered on an acknowledged channel = "sent".
            return {"outcome": "sent", "mode_before": mode_before}
        return {"outcome": "no_response", "mode_before": mode_before}

    def apply_setting(self, identifier, value):
        """Writes one control setting and waits up to confirm_timeout for the AirPods' answer. Never repeats it.
        outcome: confirmed (answer = value), different (answer with another value), sent (written, no answer -
        not proof it was applied), send_failed (the write itself failed)."""
        try:
            self._send(pkt_control(identifier, value))
        except OSError as e:
            return {"outcome": "send_failed", "error": "%s: %s" % (type(e).__name__, e)}
        seen = []

        def done(p):
            if p["type"] == "control" and p["id"] == identifier:
                seen.append(p["value"])
                return p["value"] == value
            return False
        try:
            reply = self.pump(self.confirm_timeout, until=done)
        except (OSError, ConnectionError) as e:
            # the write went out; the channel died while waiting for an answer
            return {"outcome": "sent", "echo": None, "error_after_send": "%s: %s" % (type(e).__name__, e)}
        if reply is not None:
            return {"outcome": "confirmed", "echo": value}
        if seen:
            return {"outcome": "different", "echo": seen[-1]}
        return {"outcome": "sent", "echo": None}

    def snapshot(self):
        return {"mode": self.mode, "allow_off": self.allow_off, "rx_packets": self.rx_packets, "handshake_ok": self.handshake_ok,
                "battery": [self.battery[k] for k in ("left", "right", "case") if k in self.battery], "ear": self.ear,
                "controls": {"%02x" % k: v for k, v in sorted(self.controls.items())}}


def connect_with_retry(mac, deadline, clock=time.monotonic, connect=L2capChannel.connect, log=emit):
    """AirPods may refuse the first pages while they still consider the Windows link alive: retry until deadline."""
    attempt = 0
    last = None
    while True:
        attempt += 1
        left = deadline - clock()
        if left <= 0:
            raise last or TimeoutError("connect deadline reached")
        log("STAGE connect attempt=%d" % attempt)
        try:
            return connect(mac, L2CAP_PSM, min(8.0, left))
        except OSError as e:
            last = e
            retriable = e.errno in (errno.ECONNREFUSED, errno.EHOSTDOWN, errno.EHOSTUNREACH, errno.ETIMEDOUT,
                                    errno.EBUSY, errno.ECONNRESET, errno.EAGAIN, errno.EINPROGRESS, None) \
                or isinstance(e, socket.timeout)
            log("STAGE connect_failed attempt=%d errno=%s" % (attempt, e.errno))
            if not retriable:
                raise
            time.sleep(min(1.0, max(0.0, deadline - clock())))


def run(args, connect=L2capChannel.connect):
    result = {"ok": False, "version": SCRIPT_VERSION, "requested": None}
    mode_value = MODES.get(args.mode) if args.mode else None
    result["requested"] = mode_value
    ch = None
    try:
        deadline = time.monotonic() + args.connect_timeout
        ch = connect_with_retry(args.mac, deadline, connect=connect)
        s = Session(ch, settle=args.settle, confirm_timeout=args.confirm_timeout, state_timeout=args.state_timeout)
        s.trace = args.trace
        s.feature_flags = int(args.features, 16)
        s.take_ownership = args.own
        s.start()
        if mode_value is None:
            result.update({"ok": True, "outcome": "read_only"})
        else:
            r = s.set_mode(mode_value, args.allow_off)
            result.update(r)
            result["ok"] = r["outcome"] in ("confirmed", "sent")
        result.update(s.snapshot())
    except OSError as e:
        result.update({"outcome": "connect_failed" if ch is None else "transport_error",
                       "errno": e.errno, "error": "%s: %s" % (type(e).__name__, e)})
    except Exception as e:  # noqa: BLE001 - report everything to the Windows side
        result.update({"outcome": "error", "error": "%s: %s" % (type(e).__name__, e)})
    finally:
        if ch is not None:
            ch.close()
    emit("RESULT " + json.dumps(result, sort_keys=True))
    return 0 if result["ok"] else 1


def selftest():
    """Packet bytes for cross-checking against the C# implementation."""
    out = {
        "handshake": pkt_handshake().hex(), "feature_flags": pkt_feature_flags().hex(),
        "request_notifications": pkt_request_notifications().hex(),
        "allow_off_on": pkt_allow_off(True).hex(), "allow_off_off": pkt_allow_off(False).hex(),
        "cycle_off_anc_transparency": pkt_mode_cycle(0x07).hex(), "cycle_all": pkt_mode_cycle(0x0F).hex(),
    }
    for name, v in MODES.items():
        out["mode_" + name] = pkt_listening_mode(v).hex()
    print(json.dumps(out, sort_keys=True))
    return 0


# ---------------------------------------------------------------- LE scan (adapter test)
# Apple Continuity "Proximity Pairing": manufacturer data, company 0x004C, payload
#   0 type 0x07 | 1 length | 2 prefix | 3-4 model id (little-endian) | 5 status | 6 battery nibbles ...
# Same layout as Core/Protocol/ProximityPairing.cs.

def parse_ad(data):
    """Yields (ad_type, payload) from advertising data."""
    i = 0
    while i < len(data):
        n = data[i]
        if n == 0 or i + 1 + n > len(data):
            return
        yield data[i + 1], bytes(data[i + 2:i + 1 + n])
        i += 1 + n


def proximity_model(ad):
    """Model id from an Apple proximity-pairing advertisement, or None."""
    for t, p in parse_ad(ad):
        if t == 0xFF and len(p) >= 7 and p[0] == 0x4C and p[1] == 0x00 and p[2] == 0x07:
            return p[5] | (p[6] << 8)
    return None


def parse_le_reports(pkt):
    """HCI event packet -> list of (address, rssi, adv_data). Legacy (0x02) and extended (0x0D) reports."""
    out = []
    if len(pkt) < 5 or pkt[0] != 0x04 or pkt[1] != 0x3E:
        return out
    sub, body = pkt[3], pkt[4:]
    if not body:
        return out
    num, i = body[0], 1
    for _ in range(num):
        if sub == 0x02:
            if i + 9 > len(body):
                break
            addr = body[i + 2:i + 8]
            ln = body[i + 8]
            data = body[i + 9:i + 9 + ln]
            rssi = body[i + 9 + ln] if i + 9 + ln < len(body) else 0
            i += 10 + ln
        elif sub == 0x0D:
            if i + 24 > len(body):
                break
            addr = body[i + 3:i + 9]
            rssi = body[i + 13]
            ln = body[i + 23]
            data = body[i + 24:i + 24 + ln]
            i += 24 + ln
        else:
            break
        rssi = rssi - 256 if rssi > 127 else rssi
        out.append((":".join("%02X" % b for b in reversed(addr)), rssi, bytes(data)))
    return out


HCI_FILTER_EVENTS = struct.pack("<IIIH2x", 1 << 4, 0xFFFFFFFF, 0xFFFFFFFF, 0)


def scan(seconds, dev=0, sock=None):
    """Sniffs LE advertising reports on hciN (discovery itself is started by 'btmgmt find -l')."""
    result = {"ok": True, "outcome": "scan", "seconds": seconds, "advertisers": 0, "apple_proximity": []}
    seen, apple = set(), {}
    try:
        if sock is None:
            sock = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_RAW, socket.BTPROTO_HCI)
            sock.bind((dev,))
            # struct hci_ufilter { u32 type_mask; u32 event_mask[2]; u16 opcode; } is 16 bytes with padding; the kernel
            # rejects shorter buffers with EINVAL. HCI event packets only, all events.
            sock.setsockopt(socket.SOL_HCI, socket.HCI_FILTER, HCI_FILTER_EVENTS)
        sock.settimeout(0.5)
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            try:
                pkt = sock.recv(512)
            except socket.timeout:
                continue
            for addr, rssi, data in parse_le_reports(pkt):
                seen.add(addr)
                m = proximity_model(data)
                if m is not None:
                    a = apple.setdefault(addr, {"address": addr, "model": "0x%04X" % m, "rssi": rssi, "count": 0})
                    a["count"] += 1
                    a["rssi"] = max(a["rssi"], rssi)
    except OSError as e:
        result.update({"ok": False, "outcome": "scan_failed", "error": "%s: %s" % (type(e).__name__, e)})
    finally:
        if sock is not None:
            sock.close()
    result["advertisers"] = len(seen)
    result["apple_proximity"] = sorted(apple.values(), key=lambda a: -a["rssi"])
    emit("SCAN " + json.dumps(result, sort_keys=True))
    return 0 if result["ok"] else 1


def main(argv=None):
    ap = argparse.ArgumentParser(description="AirPods AAP bridge (L2CAP PSM 0x1001)")
    ap.add_argument("--mac", help="AirPods Bluetooth address, e.g. A1:B2:C3:D4:E5:F6")
    ap.add_argument("--mode", choices=sorted(MODES), help="listening mode to set; omit for read-only status")
    ap.add_argument("--allow-off", action="store_true", help="send 'Allow Off Option' (0x34=01) before Off")
    ap.add_argument("--connect-timeout", type=float, default=12.0)
    ap.add_argument("--confirm-timeout", type=float, default=1.5, help="seconds to wait for an echo (none comes without A2DP)")
    ap.add_argument("--state-timeout", type=float, default=0.0, help="seconds to wait for the AirPods' current mode before commanding")
    ap.add_argument("--settle", type=float, default=1.0, help="seconds to collect initial notifications")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--trace", action="store_true", help="print every AAP packet as 'PKT rx|tx <hex>'")
    ap.add_argument("--features", default="d7", help="feature byte of the 0x4D packet (hex, default d7)")
    ap.add_argument("--own", action="store_true", help="send control 0x06 'owns connection' = 01 after init")
    ap.add_argument("--scan", type=float, metavar="SECONDS", help="sniff LE advertisements on hci0, report Apple proximity beacons")
    args = ap.parse_args(argv)
    if args.selftest:
        return selftest()
    if args.scan:
        return scan(args.scan)
    if not args.mac:
        ap.error("--mac is required")
    return run(args)


if __name__ == "__main__":
    sys.exit(main())
