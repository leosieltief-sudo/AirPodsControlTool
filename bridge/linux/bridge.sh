#!/bin/sh
# AirPods Control - WSL2 bridge helper (runs as root inside the 'airpods-bridge' Alpine distribution).
#
#   bridge.sh provision [--mediatek]         install packages (bluez, btmgmt, dbus, python3)
#   bridge.sh install-key ADAPTER DEVICE KEYHEX [NAME]   store the Windows BR/EDR link key for BlueZ
#   bridge.sh adapter-test [--hci-timeout S] check that the attached USB adapter comes up as hci0
#   bridge.sh bluez-up / bluez-down         start/stop bluetoothd (fallback used by aapd.py)
#   bridge.sh run --mac MAC [--mode M] [--allow-off] [--hci-timeout S] [--a2dp] [--trace]   switch mode / read status
#     outcome "sent": delivered on an acknowledged AAP channel, AirPods give no echo without A2DP (they do apply it)
#
# Every command ends with exactly one line "RESULT {json}". Progress lines start with "STAGE ".
set -u
DIR="$(cd "$(dirname "$0")" && pwd)"
HCI_TIMEOUT=4

result() { echo "RESULT $1"; }
fail() { result "{\"ok\": false, \"outcome\": \"$1\", \"error\": \"$2\"}"; exit 1; }
stage() { echo "STAGE $*"; }

wait_hci() {
    # The adapter appears as hci0 once btusb has bound to the usbip-attached device (and loaded firmware).
    modprobe btusb 2>/dev/null || true
    t=0
    limit=$(awk "BEGIN{print int($HCI_TIMEOUT*5)}")
    while [ ! -e /sys/class/bluetooth/hci0 ]; do
        t=$((t+1))
        [ "$t" -gt "$limit" ] && return 1
        sleep 0.2
    done
    return 0
}

hci_diag() {
    # Explains *why* hci0 is missing: no USB device, no btusb, or driver bound without HCI (e.g. firmware/MTK).
    usb="$(ls /sys/bus/usb/devices 2>/dev/null | tr '\n' ' ')"
    bound="$(ls /sys/bus/usb/drivers/btusb 2>/dev/null | grep -c ':' || true)"
    mod="$(grep -c '^btusb ' /proc/modules 2>/dev/null || true)"
    dm="$(dmesg 2>/dev/null | grep -iE 'bluetooth|btusb|hci0|btmtk|usb [0-9]' | tail -n 6 | tr '"\\' "''" | tr '\n' '|')"
    echo "{\"ok\": false, \"outcome\": \"adapter_not_ready\", \"error\": \"hci0 did not appear within ${HCI_TIMEOUT}s\", \"kernel\": \"$(uname -r)\", \"usb_devices\": \"$usb\", \"btusb_loaded\": \"$mod\", \"btusb_bound_interfaces\": \"$bound\", \"dmesg\": \"$dm\"}"
}

start_bluez() {
    mkdir -p /run/dbus /var/lib/bluetooth
    [ -f /etc/machine-id ] || dbus-uuidgen --ensure=/etc/machine-id 2>/dev/null || true
    if ! pgrep -x dbus-daemon >/dev/null 2>&1; then
        rm -f /run/dbus/dbus.pid
        dbus-daemon --system --fork || return 1
    else
        # pick up policies of packages installed while dbus was running (bluealsa.conf)
        dbus-send --system --type=method_call --dest=org.freedesktop.DBus / org.freedesktop.DBus.ReloadConfig >/dev/null 2>&1 || true
    fi
    pkill -x bluetoothd 2>/dev/null || true
    # 'policy' would auto-reconnect audio profiles; this bridge only needs the AAP channel.
    /usr/lib/bluetooth/bluetoothd -n --noplugin=policy,input,hog,network,sap,autopair >/tmp/bluetoothd.log 2>&1 &
    i=0
    while ! btmgmt --index 0 info >/dev/null 2>&1; do
        i=$((i+1)); [ "$i" -gt 25 ] && return 1; sleep 0.2
    done
    # Some controllers (MediaTek MT7922 on the test PC) re-run their setup when bluetoothd takes over, and a single
    # "power on" sent in that window is lost. Repeat it until the controller reports powered (up to ~12 s).
    i=0
    while ! timeout 3 btmgmt --index 0 info 2>/dev/null | grep 'current settings:' | grep -qw powered; do
        i=$((i+1)); [ "$i" -gt 12 ] && return 1
        timeout 3 btmgmt --index 0 power on >/dev/null 2>&1 || true
        sleep 1
    done
    return 0
}

stop_bluez() { pkill -x bluealsa 2>/dev/null || true; pkill -x bluetoothd 2>/dev/null || true; }

A2DP_SINK_UUID=0000110b-0000-1000-8000-00805f9b34fb

# Without an audio connection AirPods Pro 3 apply 0x0D commands but send no 0x09 packets (no current mode, no echo).
# With A2DP they report their state and echo commands (test PC, 14.11.2026). Optional (run --a2dp), costs ~4-5 s.
# bluealsa registers the A2DP *source* endpoints (we play nothing, the connection itself is enough).
start_a2dp() {
    dev="/org/bluez/hci0/dev_$(echo "$1" | tr ':' '_')"
    command -v bluealsa >/dev/null 2>&1 || { A2DP_ERR="bluez-alsa is not installed (bridge.sh provision --a2dp)"; return 1; }
    pgrep -x bluealsa >/dev/null 2>&1 || { bluealsa -p a2dp-source >/tmp/bluealsa.log 2>&1 & }
    i=0
    while ! dbus-send --system --print-reply --dest=org.freedesktop.DBus /org/freedesktop/DBus \
            org.freedesktop.DBus.NameHasOwner string:org.bluealsa 2>/dev/null | grep -q 'boolean true'; do
        i=$((i+1)); [ "$i" -gt 25 ] && { A2DP_ERR="bluealsa did not start: $(tail -n 1 /tmp/bluealsa.log 2>/dev/null)"; return 1; }
        sleep 0.2
    done
    # only the A2DP profile (fast); first contact has no SDP cache yet -> full Connect (also resolves services)
    if ! timeout 20 dbus-send --system --print-reply --reply-timeout=19000 --dest=org.bluez "$dev" \
            org.bluez.Device1.ConnectProfile string:$A2DP_SINK_UUID >/tmp/a2dp.log 2>&1; then
        timeout 25 dbus-send --system --print-reply --reply-timeout=24000 --dest=org.bluez "$dev" \
            org.bluez.Device1.Connect >/tmp/a2dp.log 2>&1 \
            || { A2DP_ERR="A2DP connect failed: $(grep -o 'Error[^"]*.*' /tmp/a2dp.log | head -n 1 | tr '"\\' "''")"; return 1; }
    fi
    return 0
}

stop_a2dp() {
    [ -n "${dev:-}" ] && timeout 5 dbus-send --system --print-reply --dest=org.bluez "$dev" org.bluez.Device1.Disconnect >/dev/null 2>&1
    pkill -x bluealsa 2>/dev/null || true
}

cmd="${1:-}"; [ $# -gt 0 ] && shift
case "$cmd" in
provision)
    stage packages
    pk="bluez bluez-btmgmt dbus python3"
    for o in "$@"; do
        case "$o" in
            --mediatek) pk="$pk linux-firmware-mediatek" ;;
            --a2dp) pk="$pk bluez-alsa" ;;   # only for the optional confirmed mode (run --a2dp)
        esac
    done
    apk update >/dev/null 2>&1 || fail provision_failed "apk update failed (network?)"
    # shellcheck disable=SC2086
    apk add --no-cache $pk >/tmp/apk.log 2>&1 || fail provision_failed "apk add failed: $(tail -n 2 /tmp/apk.log | tr '\n"' ' ')"
    bt=$(python3 -c 'import socket;print(hasattr(socket,"AF_BLUETOOTH"))' 2>/dev/null)
    result "{\"ok\": true, \"outcome\": \"provisioned\", \"python_af_bluetooth\": \"$bt\"}"
    ;;
install-key)
    adapter="$1"; device="$2"; key="$3"; name="${4:-AirPods}"
    # "-" = read the key from stdin (keeps it out of process listings)
    [ "$key" = "-" ] && read -r key
    echo "$key" | grep -qE '^[0-9A-Fa-f]{32}$' || fail bad_key "link key must be 32 hex digits"
    echo "$adapter$device" | grep -qE '^([0-9A-F]{2}:){5}[0-9A-F]{2}([0-9A-F]{2}:){5}[0-9A-F]{2}$' || fail bad_arguments "addresses must be AA:BB:CC:DD:EE:FF"
    d="/var/lib/bluetooth/$adapter/$device"
    mkdir -p "$d" && chmod 700 "/var/lib/bluetooth/$adapter" "$d"
    umask 077
    cat >"$d/info" <<EOF
[General]
Name=$name
SupportedTechnologies=BR/EDR;
Trusted=true
Blocked=false

[LinkKey]
Key=$key
Type=4
PINLength=0
EOF
    result "{\"ok\": true, \"outcome\": \"key_installed\"}"
    ;;
adapter-test|run)
    mac=""; mode=""; extra=""; scan=0; a2dp=0
    while [ $# -gt 0 ]; do
        case "$1" in
            --mac) mac="$2"; shift 2 ;;
            --mode) mode="$2"; shift 2 ;;
            --allow-off) extra="$extra --allow-off"; shift ;;
            --hci-timeout) HCI_TIMEOUT="$2"; shift 2 ;;
            --scan) scan="$2"; shift 2 ;;
            --settle) extra="$extra --settle $2"; shift 2 ;;
            --trace) extra="$extra --trace"; shift ;;
            --own) extra="$extra --own"; shift ;;
            # optional: with an A2DP connection the AirPods also report their mode and echo commands (+4-5 s);
            # needs bluez-alsa (provision --a2dp)
            --a2dp) a2dp=1; extra="$extra --state-timeout 5 --confirm-timeout 4"; shift ;;
            --features) extra="$extra --features $2"; shift 2 ;;
            *) fail bad_arguments "unknown argument $1" ;;
        esac
    done
    stage wait_hci
    if ! wait_hci; then result "$(hci_diag)"; exit 1; fi
    stage bluez
    if ! start_bluez; then
        log="$(tail -n 3 /tmp/bluetoothd.log 2>/dev/null | tr '\n"' ' ')"
        stop_bluez; fail bluez_failed "bluetoothd/power on failed: $log"
    fi
    if [ "$cmd" = "adapter-test" ]; then
        addr="$(btmgmt --index 0 info 2>/dev/null | sed -n 's/.*addr \([0-9A-F:]*\).*/\1/p' | head -n 1)"
        name="$(cat /sys/class/bluetooth/hci0/device/../product 2>/dev/null | tr -d '"\\')"
        drv="$(basename "$(readlink /sys/class/bluetooth/hci0/device/driver 2>/dev/null)" 2>/dev/null)"
        scanjson="null"
        if [ "$scan" != "0" ]; then
            stage scan
            # bluetoothd/mgmt runs LE discovery; aap_bridge.py only listens to the HCI advertising reports
            python3 "$DIR/aap_bridge.py" --scan "$scan" >/tmp/scan.out 2>&1 &
            sp=$!
            timeout "$scan" btmgmt --index 0 find -l >/tmp/find.out 2>&1 || true
            wait "$sp" 2>/dev/null
            scanjson="$(sed -n 's/^SCAN //p' /tmp/scan.out | tail -n 1)"
            [ -n "$scanjson" ] || scanjson="{\"ok\": false, \"error\": \"$(tail -n 2 /tmp/scan.out | tr '\n"\\' "  '")\"}"
        fi
        stop_bluez
        result "{\"ok\": true, \"outcome\": \"adapter_ready\", \"address\": \"$addr\", \"usb_product\": \"$name\", \"driver\": \"$drv\", \"kernel\": \"$(uname -r)\", \"scan\": $scanjson}"
        exit 0
    fi
    [ -n "$mac" ] || { stop_bluez; fail bad_arguments "--mac required"; }
    if [ "$a2dp" = "1" ]; then
        stage a2dp
        A2DP_ERR=""
        if ! start_a2dp "$mac"; then
            stop_a2dp; stop_bluez
            fail a2dp_failed "${A2DP_ERR:-A2DP connect failed}"
        fi
    fi
    stage aap
    if [ -n "$mode" ]; then
        # shellcheck disable=SC2086
        python3 "$DIR/aap_bridge.py" --mac "$mac" --mode "$mode" $extra
    else
        # shellcheck disable=SC2086
        python3 "$DIR/aap_bridge.py" --mac "$mac" $extra
    fi
    rc=$?
    stop_a2dp
    stop_bluez
    exit $rc
    ;;
bluez-up)
    # fallback for aapd.py when the adapter cannot be brought up over the management socket directly
    while [ $# -gt 0 ]; do
        case "$1" in
            --hci-timeout) HCI_TIMEOUT="$2"; shift 2 ;;
            *) shift ;;
        esac
    done
    if ! wait_hci; then result "$(hci_diag)"; exit 1; fi
    if ! start_bluez; then
        log="$(tail -n 3 /tmp/bluetoothd.log 2>/dev/null | tr '\n"' ' ')"
        stop_bluez; fail bluez_failed "bluetoothd/power on failed: $log"
    fi
    result "{\"ok\": true, \"outcome\": \"bluez_up\"}"
    ;;
bluez-down)
    stop_bluez
    result "{\"ok\": true, \"outcome\": \"bluez_down\"}"
    ;;
version)
    result "{\"ok\": true, \"outcome\": \"version\", \"version\": $(python3 "$DIR/aap_bridge.py" --selftest >/dev/null 2>&1 && echo 1 || echo 0)}"
    ;;
*)
    fail bad_arguments "usage: bridge.sh provision|install-key|adapter-test|run"
    ;;
esac
