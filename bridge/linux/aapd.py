#!/usr/bin/env python3
"""
AirPods Control - persistent WSL2 bridge daemon.

Started once by the Windows app as `wsl.exe -d airpods-bridge -u root --exec python3 /opt/airpods/aapd.py` and
kept running (it also keeps the WSL VM alive). Talks JSON lines over stdin/stdout - no ports, no pipes, no extra
packages. The USB adapter is still attached/detached by the Windows side (usbipd); the daemon only reacts to the
adapter appearing as an HCI index.

Requests (stdin, one JSON object per line):
    {"id": 1, "op": "ping"}
    {"id": 2, "op": "switch", "mac": "AA:BB:..", "mode": "anc", "allow_off": false,
     "hci_timeout": 10, "connect_timeout": 12, "settle": 0.5, "confirm_timeout": 0.2, "trace": false}
    {"id": 3, "op": "read", "mac": "AA:BB:..", "listen": 1.5}   exact battery / ear only, no command
        optional "wait_controls": [26, 52, 22] - stop listening early once these control ids were reported
    {"id": 6, "op": "configure", "mac": "AA:BB:..", "allow_off": true, "cycle": 7, "confirm_timeout": 1.0}
        writes 0x34 = 01 (allow Off) and/or 0x1A = cycle mask on the open channel, each at most once, never 0x0D;
        reply "settings": {"allow_off": {"outcome": ..., "echo": ...}, "cycle": {...}}
    {"id": 4, "op": "release"}          close the AAP channel, drop the ACL link (call before usbipd detach)
    {"id": 5, "op": "events", "op_id": "op7-ab12"}   kernel events (ACL up/down + reason, index) of that operation
    Every request may carry "op_id" (the Windows operation id); it is echoed in the reply.
    {"id": 4, "op": "quit"}
Output (stdout, one JSON object per line):
    {"type": "ready", "version": 3}
    {"type": "stage", "id": 2, "stage": "powered", "t": 0.91}
    {"type": "reply", "id": 2, "ok": true, "outcome": "sent", "t": {...}, ...}   same fields as aap_bridge.py RESULT

Adapter bring-up does not use bluetoothd/dbus: the Windows link key (stored by `bridge.sh install-key` in BlueZ's
/var/lib/bluetooth/<adapter>/<device>/info) is loaded straight into the kernel over the Bluetooth management socket
(the same MGMT_OP_LOAD_LINK_KEYS bluetoothd itself uses), then the adapter is powered on. If that fails the daemon
falls back to `bridge.sh bluez-up` (bluetoothd). Opcodes: BlueZ doc/mgmt-api.txt.
"""
import ctypes
import ctypes.util
import json
import os
import select
import struct
import subprocess
import sys
import threading
import time
from collections import deque

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import aap_bridge as aap  # noqa: E402  (same directory, stdlib only)

DAEMON_VERSION = 5
DIR = os.path.dirname(os.path.abspath(__file__))
BT_STORAGE = "/var/lib/bluetooth"

# ---------------------------------------------------------------- Bluetooth management socket
AF_BLUETOOTH = 31
BTPROTO_HCI = 1
SOCK_RAW = 3
SOCK_CLOEXEC = 0o2000000
HCI_CHANNEL_CONTROL = 3
INDEX_NONE = 0xFFFF

EV_CMD_COMPLETE = 0x0001
EV_CMD_STATUS = 0x0002
EV_INDEX_REMOVED = 0x0005

OP_READ_INDEX_LIST = 0x0003
OP_READ_INFO = 0x0004
OP_SET_POWERED = 0x0005
OP_SET_BONDABLE = 0x0009
OP_SET_SSP = 0x000B
OP_LOAD_LINK_KEYS = 0x0012
OP_DISCONNECT = 0x0014
OP_SET_BREDR = 0x002A

SETTING_POWERED = 1 << 0


def mac_bytes(mac):
    """'AA:BB:CC:DD:EE:FF' -> little-endian bdaddr bytes."""
    return bytes(reversed(bytes.fromhex(mac.replace(":", ""))))


def mac_str(b):
    return ":".join("%02X" % x for x in reversed(b))


def pack_link_keys(keys, debug_keys=False):
    """MGMT_OP_LOAD_LINK_KEYS parameters: debug(1) count(2) {bdaddr(6) addr_type(1)=BR/EDR type(1) key(16) pin_len(1)}*"""
    out = struct.pack("<BH", 1 if debug_keys else 0, len(keys))
    for mac, key_type, key, pin_len in keys:
        if len(key) != 16:
            raise ValueError("link key must be 16 bytes")
        out += mac_bytes(mac) + struct.pack("<BB", 0, key_type) + key + struct.pack("<B", pin_len)
    return out


def parse_info_file(text):
    """BlueZ storage 'info' file -> (key bytes, type, pin_len) or None. Key is stored as hex in transmission order
    (bluetoothd's str2buf reads it byte by byte, so do we)."""
    section, values = None, {}
    for raw in text.splitlines():
        line = raw.strip()
        if line.startswith("[") and line.endswith("]"):
            section = line[1:-1]
        elif "=" in line and section == "LinkKey":
            k, v = line.split("=", 1)
            values[k.strip()] = v.strip()
    try:
        key = bytes.fromhex(values["Key"])
        return key, int(values.get("Type", "4")), int(values.get("PINLength", "0"))
    except (KeyError, ValueError):
        return None


def load_stored_keys(adapter_mac, root=BT_STORAGE):
    """All link keys BlueZ storage holds for this adapter: [(device_mac, type, key, pin_len)]."""
    keys = []
    d = os.path.join(root, adapter_mac.upper())
    try:
        names = sorted(os.listdir(d))
    except OSError:
        return keys
    for name in names:
        if len(name) != 17 or name.count(":") != 5:
            continue
        try:
            with open(os.path.join(d, name, "info"), encoding="utf-8") as f:
                parsed = parse_info_file(f.read())
        except OSError:
            continue
        if parsed and len(parsed[0]) == 16:
            keys.append((name.upper(), parsed[1], parsed[0], parsed[2]))
    return keys


class MgmtError(Exception):
    def __init__(self, opcode, status):
        super().__init__("mgmt opcode 0x%04X failed with status 0x%02X" % (opcode, status))
        self.opcode, self.status = opcode, status


class Mgmt:
    """Minimal client for the kernel's Bluetooth management interface (HCI_CHANNEL_CONTROL)."""

    def __init__(self, fd=None):
        if fd is None:
            libc = ctypes.CDLL(ctypes.util.find_library("c") or None, use_errno=True)
            fd = libc.socket(AF_BLUETOOTH, SOCK_RAW | SOCK_CLOEXEC, BTPROTO_HCI)
            if fd < 0:
                e = ctypes.get_errno()
                raise OSError(e, "mgmt socket: " + os.strerror(e))
            addr = ctypes.create_string_buffer(struct.pack("<HHH", AF_BLUETOOTH, INDEX_NONE, HCI_CHANNEL_CONTROL), 6)
            if libc.bind(fd, addr, 6) != 0:
                e = ctypes.get_errno()
                os.close(fd)
                raise OSError(e, "mgmt bind: " + os.strerror(e))
        self.fd = fd

    def close(self):
        try:
            os.close(self.fd)
        except OSError:
            pass

    def read_event(self, timeout):
        r, _, _ = select.select([self.fd], [], [], max(0.0, timeout))
        if not r:
            return None
        data = os.read(self.fd, 1024)
        if len(data) < 6:
            return None
        code, index, length = struct.unpack_from("<HHH", data)
        return code, index, data[6:6 + length]

    def command(self, opcode, index, params=b"", timeout=5.0):
        os.write(self.fd, struct.pack("<HHH", opcode, index, len(params)) + params)
        end = time.monotonic() + timeout
        while True:
            left = end - time.monotonic()
            if left <= 0:
                raise TimeoutError("mgmt opcode 0x%04X: no reply" % opcode)
            ev = self.read_event(left)
            if ev is None:
                continue
            code, idx, data = ev
            if code in (EV_CMD_COMPLETE, EV_CMD_STATUS) and len(data) >= 3 and idx == index:
                op, status = struct.unpack_from("<HB", data)
                if op == opcode:
                    if status != 0:
                        raise MgmtError(opcode, status)
                    if code == EV_CMD_COMPLETE:
                        return data[3:]
                    continue            # status 0 = pending, the complete event follows
            # other events (index added/removed, new settings) are not needed: callers re-read the index list

    def index_list(self):
        data = self.command(OP_READ_INDEX_LIST, INDEX_NONE)
        n = struct.unpack_from("<H", data)[0]
        return list(struct.unpack_from("<%dH" % n, data, 2))

    def read_info(self, index):
        data = self.command(OP_READ_INFO, index)
        return {"address": mac_str(data[0:6]), "settings": struct.unpack_from("<I", data, 13)[0]}

    def set_setting(self, opcode, index, value, timeout=5.0):
        data = self.command(opcode, index, struct.pack("<B", 1 if value else 0), timeout)
        return struct.unpack_from("<I", data)[0] if len(data) >= 4 else 0

    def load_link_keys(self, index, keys):
        self.command(OP_LOAD_LINK_KEYS, index, pack_link_keys(keys))

    def disconnect(self, index, mac):
        self.command(OP_DISCONNECT, index, mac_bytes(mac) + b"\x00", timeout=3.0)


# ---------------------------------------------------------------- kernel events (trace)
DISCONNECT_REASONS = {0: "unspecified", 1: "connection_timeout", 2: "local_host_terminated", 3: "remote_terminated",
                      4: "auth_failure", 5: "local_host_suspended"}


def decode_mgmt_event(code, index, data):
    """(name, detail) for the management events that explain link drops, or (None, None)."""
    if code == 0x0004:
        return "index_added", "hci%d" % index
    if code == 0x0005:
        return "index_removed", "hci%d" % index
    if code == 0x0006 and len(data) >= 4:
        return "new_settings", "hci%d powered=%d" % (index, struct.unpack_from("<I", data)[0] & 1)
    if code == 0x0009 and len(data) >= 7:
        return "new_link_key", "addr=%s" % mac_str(data[1:7])
    if code == 0x000B and len(data) >= 7:
        return "acl_connected", "addr=%s type=%d" % (mac_str(data[0:6]), data[6])
    if code == 0x000C and len(data) >= 8:
        return "acl_disconnected", "addr=%s reason=%s" % (mac_str(data[0:6]), DISCONNECT_REASONS.get(data[7], "0x%02X" % data[7]))
    if code == 0x000D and len(data) >= 8:
        return "connect_failed", "addr=%s status=0x%02X" % (mac_str(data[0:6]), data[7])
    if code == 0x0011 and len(data) >= 8:
        return "auth_failed", "addr=%s status=0x%02X" % (mac_str(data[0:6]), data[7])
    return None, None


class EventMonitor:
    """Reads management events on its own socket in a background thread and time-stamps them when they happen
    (the command socket only sees events while a command is waiting)."""

    def __init__(self, mgmt_factory=None, clock=time.monotonic, start=True):
        self.clock = clock
        self._events = deque(maxlen=2000)
        self._cv = threading.Condition()
        self.error = None
        self._m = None
        if not start:
            return
        try:
            self._m = (mgmt_factory or Mgmt)()
        except OSError as e:
            self.error = str(e)
            return
        threading.Thread(target=self._run, name="mgmt-events", daemon=True).start()

    def _run(self):
        while True:
            try:
                ev = self._m.read_event(1.0)
            except OSError as e:
                self.error = str(e)
                return
            if ev is not None:
                self.add(self.clock(), *decode_mgmt_event(*ev))

    def add(self, t, name, detail):
        if name is None:
            return
        with self._cv:
            self._events.append((t, name, detail))
            self._cv.notify_all()

    def since(self, t):
        with self._cv:
            return [e for e in self._events if e[0] >= t]

    def wait_for(self, name, since, timeout):
        end = self.clock() + timeout
        with self._cv:
            while not any(e[1] == name and e[0] >= since for e in self._events):
                left = end - self.clock()
                if left <= 0:
                    return False
                self._cv.wait(left)
            return True


# ---------------------------------------------------------------- daemon
def default_out(obj):
    sys.stdout.write(json.dumps(obj, sort_keys=True) + "\n")
    sys.stdout.flush()


class Daemon:
    def __init__(self, mgmt_factory=Mgmt, connect=aap.L2capChannel.connect, out=default_out, clock=time.monotonic,
                 run=subprocess.run, key_loader=None, sleep=time.sleep, monitor=None):
        self.monitor = monitor or EventMonitor(start=False)
        self.op_started = {}       # operation id (from Windows) -> clock of its first request
        self._req_t0 = clock()
        self._mgmt_factory = mgmt_factory
        self._mgmt = None
        self.connect = connect
        self.out = out
        self.clock = clock
        self.run = run
        self.key_loader = key_loader or load_stored_keys
        self.sleep = sleep
        self.index = None
        self.adapter = None
        self.stack = None          # "mgmt" or "bluez" once the adapter is up
        self.chan = None
        self.session = None
        self.chan_mac = None
        self.last_tx = 0.0
        self.last_request = clock()
        self.modprobed = False

    # ---- helpers
    @property
    def mgmt(self):
        if self._mgmt is None:
            self._mgmt = self._mgmt_factory()
        return self._mgmt

    def _stage(self, rid, t0, stages, name):
        stages[name] = round(self.clock() - t0, 3)
        self.out({"type": "stage", "id": rid, "stage": name, "t": stages[name]})

    def _ensure_btusb(self):
        if self.modprobed:
            return
        try:
            self.run(["modprobe", "btusb"], timeout=10, capture_output=True)
        except (OSError, subprocess.SubprocessError):
            pass
        self.modprobed = True

    def _close_channel(self):
        if self.chan is not None:
            # give a just-written command time to leave the controller before the link goes down
            wait = 0.15 - (self.clock() - self.last_tx)
            if wait > 0:
                self.sleep(wait)
            self.chan.close()
        self.chan = self.session = self.chan_mac = None

    def _forget_adapter(self):
        self._close_channel()
        self.index = self.adapter = None
        if self.stack == "bluez":
            self._bluez("bluez-down", 10)
        self.stack = None

    def _bluez(self, cmd, timeout, extra=()):
        try:
            r = self.run(["/bin/sh", os.path.join(DIR, "bridge.sh"), cmd] + list(extra), timeout=timeout,
                         capture_output=True, text=True)
            res = None
            for line in (r.stdout or "").splitlines():
                if line.startswith("RESULT "):
                    res = json.loads(line[7:])
            return res or {"ok": False, "error": "bridge.sh %s printed no result" % cmd}
        except (OSError, subprocess.SubprocessError, ValueError) as e:
            return {"ok": False, "error": "%s: %s" % (type(e).__name__, e)}

    def wait_index(self, timeout):
        """Index of the attached adapter once its setup (firmware) is done, or None."""
        self._ensure_btusb()
        end = self.clock() + timeout
        while True:
            idx = self.mgmt.index_list()
            if idx:
                return idx[0]
            left = end - self.clock()
            if left <= 0:
                return None
            self.mgmt.read_event(min(0.25, left))   # wakes up on INDEX_ADDED (or any event), then re-check

    def power_up(self, index, rid, t0, stages):
        """Loads the stored link keys into the kernel and powers the adapter on, without bluetoothd."""
        m = self.mgmt
        info = m.read_info(index)
        self.adapter = info["address"]
        keys = self.key_loader(self.adapter)
        if not keys:
            raise LookupError("no pairing key stored for adapter %s (run Driverless setup)" % self.adapter)
        for op in (OP_SET_BREDR, OP_SET_SSP, OP_SET_BONDABLE):
            try:
                m.set_setting(op, index, True)
            except MgmtError:
                pass                     # already set / not changeable while powered: harmless
        m.load_link_keys(index, keys)
        self._stage(rid, t0, stages, "keys")
        # MediaTek MT7922 may still be re-initialising right after the index appears; retry until powered.
        last = None
        for attempt in range(8):
            try:
                settings = m.set_setting(OP_SET_POWERED, index, True, timeout=6.0)
                if settings & SETTING_POWERED:
                    return attempt + 1
            except (MgmtError, TimeoutError) as e:
                last = e
            self.sleep(0.4)
            try:
                if m.read_info(index)["settings"] & SETTING_POWERED:
                    return attempt + 1
            except (MgmtError, TimeoutError) as e:
                last = e
        raise RuntimeError("adapter did not power on: %s" % (last or "settings never reported powered"))

    def ensure_adapter(self, req, rid, t0, stages):
        if self.index is not None and self.stack is not None:
            try:
                if self.index in self.mgmt.index_list():
                    stages["adapter_reused"] = True
                    return
            except (OSError, TimeoutError, MgmtError):
                pass
            self._forget_adapter()
        idx = self.wait_index(float(req.get("hci_timeout", 10)))
        if idx is None:
            raise AdapterMissing("hci adapter did not appear within %ss" % req.get("hci_timeout", 10))
        self.index = idx
        self._stage(rid, t0, stages, "hci")
        if req.get("stack", "auto") != "bluez":
            try:
                stages["power_attempts"] = self.power_up(idx, rid, t0, stages)
                self.stack = "mgmt"
                self._stage(rid, t0, stages, "powered")
                return
            except LookupError:
                raise
            except (OSError, TimeoutError, MgmtError, RuntimeError) as e:
                stages["mgmt_error"] = str(e)
                if req.get("stack") == "mgmt":
                    raise
        r = self._bluez("bluez-up", 30, ["--hci-timeout", str(req.get("hci_timeout", 10))])
        if not r.get("ok"):
            raise RuntimeError("bluetoothd fallback failed: %s" % r.get("error"))
        self.stack = "bluez"
        self._stage(rid, t0, stages, "powered")

    def _open(self, req, mac, rid, t0, stages):
        self._close_channel()
        self.ensure_adapter(req, rid, t0, stages)

        def log(line):
            if line.startswith("STAGE "):
                self.out({"type": "stage", "id": rid, "stage": line[6:], "t": round(self.clock() - t0, 3)})
            elif line.startswith("PKT "):
                self.out({"type": "pkt", "id": rid, "line": line[4:]})
        deadline = self.clock() + float(req.get("connect_timeout", 12))
        ch = aap.connect_with_retry(mac, deadline, clock=self.clock, connect=self.connect, log=log)
        self._stage(rid, t0, stages, "connected")
        s = aap.Session(ch, clock=self.clock, settle=float(req.get("settle", 0.5)), log=log, confirm_timeout=float(req.get("confirm_timeout", 0.2)))
        s.trace = bool(req.get("trace"))
        self.chan, self.session, self.chan_mac = ch, s, mac
        s.start()
        self._stage(rid, t0, stages, "handshake")

    # ---- operations
    def op_switch(self, req):
        rid = req.get("id")
        t0 = self.clock()
        stages = {}
        mode_value = aap.MODES.get(req.get("mode") or "")
        mac = (req.get("mac") or "").upper()
        result = {"ok": False, "version": DAEMON_VERSION, "requested": mode_value, "t": stages}
        if mode_value is None or len(mac) != 17:
            result.update({"outcome": "bad_arguments", "error": "mac and mode are required"})
            return result
        connected = False
        try:
            reuse = self.session is not None and self.chan_mac == mac
            for attempt in (0, 1):
                if not reuse:
                    connected = False
                    self._open(req, mac, rid, t0, stages)
                    connected = True
                else:
                    stages["channel_reused"] = True
                    connected = True
                s = self.session
                s.confirm_timeout = float(req.get("confirm_timeout", 0.2))
                try:
                    r = s.set_mode(mode_value, bool(req.get("allow_off")))
                    break
                except (OSError, ConnectionError):
                    if not reuse or attempt:
                        raise
                    # the kept channel died (AirPods in the case, link dropped): open a fresh one once
                    self._close_channel()
                    stages["channel_reopened"] = True
                    reuse = False
            self.last_tx = self.clock()
            self._stage(rid, t0, stages, "sent")
            result.update(r)
            result["ok"] = r["outcome"] in ("confirmed", "sent")
            result.update(s.snapshot())
            result["rx"] = self._rx_since(s, t0)
        except AdapterMissing as e:
            result.update({"outcome": "adapter_not_ready", "error": str(e)})
        except LookupError as e:
            result.update({"outcome": "no_key", "error": str(e)})
        except OSError as e:
            result.update({"outcome": "transport_error" if connected else "connect_failed",
                           "errno": e.errno, "error": "%s: %s" % (type(e).__name__, e)})
            self._close_channel()
        except Exception as e:  # noqa: BLE001 - everything goes back to Windows
            result.update({"outcome": "error", "error": "%s: %s" % (type(e).__name__, e)})
            self._close_channel()
        result["stack"] = self.stack
        result["t"]["total"] = round(self.clock() - t0, 3)
        return result

    def _rx_since(self, session, t0):
        return [[round(t - t0, 3), d] for t, d in session.rx_log if t >= t0]

    def op_read(self, req):
        """Exact battery / ear / state only: opens (or reuses) the AAP channel, listens, sends no command."""
        rid = req.get("id")
        t0 = self.clock()
        stages = {}
        mac = (req.get("mac") or "").upper()
        result = {"ok": False, "version": DAEMON_VERSION, "requested": None, "t": stages}
        if len(mac) != 17:
            result.update({"outcome": "bad_arguments", "error": "mac is required"})
            return result
        connected = False
        try:
            if self.session is not None and self.chan_mac == mac:
                stages["channel_reused"] = True
            else:
                self._open(req, mac, rid, t0, stages)
            connected = True
            s = self.session
            wanted = set(int(x) for x in (req.get("wait_controls") or []))
            if wanted:
                # e.g. the press-and-hold cycle before Enable Off: stop as soon as all were reported (or listen expires)
                self._stage(rid, t0, stages, "wait_controls")
                if not wanted.issubset(s.controls):     # may already have arrived during the handshake
                    s.pump(float(req.get("listen", 1.5)), until=lambda p: wanted.issubset(s.controls))
            else:
                s.pump(float(req.get("listen", 1.5)))
            self._stage(rid, t0, stages, "listened")
            result.update(s.snapshot())
            result["ok"] = s.handshake_ok
            result["outcome"] = "read_only" if s.handshake_ok else "no_response"
            result["rx"] = self._rx_since(s, t0)
        except AdapterMissing as e:
            result.update({"outcome": "adapter_not_ready", "error": str(e)})
        except LookupError as e:
            result.update({"outcome": "no_key", "error": str(e)})
        except OSError as e:
            result.update({"outcome": "transport_error" if connected else "connect_failed",
                           "errno": e.errno, "error": "%s: %s" % (type(e).__name__, e)})
            self._close_channel()
        except Exception as e:  # noqa: BLE001
            result.update({"outcome": "error", "error": "%s: %s" % (type(e).__name__, e)})
            self._close_channel()
        result["stack"] = self.stack
        result["t"]["total"] = round(self.clock() - t0, 3)
        return result

    def op_configure(self, req):
        """Press-and-hold cycle: 0x34 = 01 (if allow_off) then 0x1A = mask on the channel opened by the preceding "read".
        Each command at most once (no repeat when the AirPods do not answer). The cycle is not sent when allowing
        Off failed or was answered with another value. Never sends a listening mode (0x0D) or the hold action (0x16)."""
        rid = req.get("id")
        t0 = self.clock()
        stages = {}
        mac = (req.get("mac") or "").upper()
        allow_off = bool(req.get("allow_off"))
        cycle = req.get("cycle")
        result = {"ok": False, "version": DAEMON_VERSION, "requested": None, "t": stages, "settings": {}}
        if len(mac) != 17 or (cycle is not None and not aap.valid_cycle(cycle)) or (not allow_off and cycle is None):
            result.update({"outcome": "bad_arguments", "error": "mac and a valid cycle mask (2+ of 0x01/0x02/0x04/0x08) or allow_off are required"})
            return result
        if self.session is None or self.chan_mac != mac:
            # deliberately no reconnect here: the state this request is based on was read on that channel
            result.update({"outcome": "no_channel", "error": "no open AAP channel to these AirPods (read first)"})
            return result
        s = self.session
        if not s.handshake_ok:
            result.update({"outcome": "no_response", "error": "the AirPods did not acknowledge the handshake"})
            return result
        s.confirm_timeout = float(req.get("confirm_timeout", 1.0))
        settings = result["settings"]
        failed = False
        if allow_off:
            self._stage(rid, t0, stages, "allow_off")
            settings["allow_off"] = r = s.apply_setting(aap.CTRL_ALLOW_OFF, 0x01)
            self.last_tx = self.clock()
            if r["outcome"] == "send_failed":
                failed = True
            elif r["outcome"] == "different":
                if cycle is not None:
                    settings["cycle"] = {"outcome": "not_sent", "error": "Allow Off was answered with 0x%02X" % r["echo"]}
                cycle = None
        if cycle is not None:
            if failed:
                settings["cycle"] = {"outcome": "not_sent", "error": "sending Allow Off failed"}
            else:
                self._stage(rid, t0, stages, "cycle")
                settings["cycle"] = r = s.apply_setting(aap.CTRL_MODE_CYCLE, int(cycle))
                self.last_tx = self.clock()
                failed = r["outcome"] == "send_failed"
        if failed:
            self._close_channel()
        self._stage(rid, t0, stages, "configured")
        result["ok"] = not failed
        result["outcome"] = "transport_error" if failed else "configured"
        result.update(s.snapshot())
        result["rx"] = self._rx_since(s, t0)
        result["stack"] = self.stack
        result["t"]["total"] = round(self.clock() - t0, 3)
        return result

    def op_release(self, req):
        t0 = self.clock()
        mac, index = self.chan_mac, self.index
        self._close_channel()
        dropped = False
        if mac and index is not None and self.stack == "mgmt":
            try:
                self.mgmt.disconnect(index, mac)   # AirPods learn at once that the link is gone, reconnect to Windows sooner
                dropped = True
            except (OSError, TimeoutError, MgmtError):
                pass
            # wait (briefly) until the kernel reports the link down, so the trace shows it before usbipd detach
            self.monitor.wait_for("acl_disconnected", t0, 1.0)
        self._forget_adapter()
        return {"ok": True, "outcome": "released", "acl_dropped": dropped, "t": {"total": round(self.clock() - t0, 3)}}

    def op_events(self, req):
        """Kernel events since the first request of this operation (times relative to this request)."""
        since = self.op_started.get(req.get("op_id"), self._req_t0)
        return {"ok": True, "outcome": "events", "monitor_error": self.monitor.error,
                "events": [[round(t - self._req_t0, 3), n, d] for t, n, d in self.monitor.since(since)]}

    def handle(self, req):
        self.last_request = self._req_t0 = self.clock()
        op_id = req.get("op_id")
        if op_id and op_id not in self.op_started:
            self.op_started[op_id] = self._req_t0
            while len(self.op_started) > 50:
                self.op_started.pop(next(iter(self.op_started)))
        op = req.get("op")
        if op == "ping":
            res = {"ok": True, "outcome": "pong", "version": DAEMON_VERSION, "channel_open": self.session is not None,
                   "monitor_error": self.monitor.error}
        elif op == "switch":
            res = self.op_switch(req)
        elif op == "read":
            res = self.op_read(req)
        elif op == "configure":
            res = self.op_configure(req)
        elif op == "events":
            res = self.op_events(req)
        elif op == "release":
            res = self.op_release(req)
        elif op == "quit":
            self._forget_adapter()
            res = {"ok": True, "outcome": "bye"}
        else:
            res = {"ok": False, "outcome": "bad_arguments", "error": "unknown op %r" % op}
        res["type"] = "reply"
        res["id"] = req.get("id")
        res["op_id"] = op_id
        self.out(res)
        return op != "quit"

    def idle_tick(self, readable):
        """Housekeeping between requests: notice adapter removal, keep reading an open AAP channel."""
        if self._mgmt is not None and self._mgmt.fd in readable:
            ev = self._mgmt.read_event(0)
            if ev is not None and ev[0] == EV_INDEX_REMOVED and ev[1] == self.index:
                self._close_channel()
                self.index = self.adapter = None
                self.stack = None if self.stack == "mgmt" else self.stack
        if self.chan is not None and self.chan.fd in readable:
            try:
                self.session.pump(0.01)
            except (OSError, ConnectionError):
                self._close_channel()

    def serve(self, stdin_fd=0, idle_exit=900.0):
        self.out({"type": "ready", "version": DAEMON_VERSION, "pid": os.getpid()})
        buf = b""
        while True:
            fds = [stdin_fd]
            if self._mgmt is not None:
                fds.append(self._mgmt.fd)
            if self.chan is not None:
                fds.append(self.chan.fd)
            r, _, _ = select.select(fds, [], [], 1.0)
            if stdin_fd in r:
                chunk = os.read(stdin_fd, 65536)
                if not chunk:
                    self._forget_adapter()
                    return 0               # Windows side went away
                buf += chunk
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    line = line.strip()
                    if line.startswith(b"\xef\xbb\xbf"):   # tolerate a UTF-8 BOM from the Windows side
                        line = line[3:]
                    if not line:
                        continue
                    try:
                        req = json.loads(line.decode("utf-8"))
                    except ValueError:
                        self.out({"type": "reply", "id": None, "ok": False, "outcome": "bad_request"})
                        continue
                    if not self.handle(req):
                        return 0
            self.idle_tick([f for f in r if f != stdin_fd])
            if idle_exit > 0 and self.chan is None and self.clock() - self.last_request > idle_exit:
                self.out({"type": "exit", "reason": "idle"})
                return 0


class AdapterMissing(Exception):
    pass


def main(argv=None):
    import argparse
    ap = argparse.ArgumentParser(description="AirPods Control bridge daemon (JSON lines on stdin/stdout)")
    ap.add_argument("--idle-exit", type=float, default=900.0, help="exit after this many idle seconds (0 = never)")
    args = ap.parse_args(argv)
    return Daemon(monitor=EventMonitor()).serve(idle_exit=args.idle_exit)


if __name__ == "__main__":
    sys.exit(main())
