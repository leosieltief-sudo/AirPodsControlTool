#!/bin/sh
# Builds the AirPods Control WSL2 kernel: the stock Microsoft WSL kernel (same tag, same config) plus
#   * MediaTek USB Bluetooth support (CONFIG_BT_HCIBTUSB_MTK) and the Bluetooth stack built in,
#   * USB IDs that the 6.6 btusb table is missing (kernel/patches/*.patch),
#   * MediaTek MT7921/MT7922/MT7925 Bluetooth firmware embedded in the kernel image
#     (the WSL kernel cannot read /lib/firmware of a distribution: the loader looks in the VM's root namespace).
# Output (in $OUT): bzImage, modules.vhdx, config, SHA256SUMS, BUILDINFO.
# Usage (inside an Alpine WSL distro, as root):  sh build-kernel.sh <out-dir> [tag]
set -eu

OUT=${1:?output directory}
TAG=${2:-linux-msft-wsl-6.6.114.1}
HERE=$(cd "$(dirname "$0")" && pwd)
SRC=/build/linux
JOBS=$(nproc)

log() { echo "== $(date +%H:%M:%S) $*"; }

apk add --no-cache build-base bc bison flex openssl-dev elfutils-dev perl python3 git linux-headers \
    ncurses-dev pahole zstd xz diffutils findutils tar coreutils e2fsprogs qemu-img util-linux kmod bash >/dev/null

if [ ! -d "$SRC/.git" ]; then
    log "clone $TAG"
    mkdir -p /build
    git clone --depth 1 -b "$TAG" https://github.com/microsoft/WSL2-Linux-Kernel.git "$SRC"
fi
cd "$SRC"
# revert sources to the tag (build outputs are kept, so a re-run is incremental)
git checkout -q -- .
COMMIT=$(git rev-parse HEAD)

log "patches"
for p in "$HERE"/patches/*.patch; do git apply --verbose "$p"; done

log "firmware"
FW=/build/firmware
rm -rf "$FW" && mkdir -p "$FW/mediatek/mt7925"
# Alpine's linux-firmware-mediatek (files from linux-firmware.git, zstd-compressed). Only used as a build input here.
apk add --no-cache linux-firmware-mediatek >/dev/null
FWPKG=$(apk info -v 2>/dev/null | grep '^linux-firmware-mediatek-' | head -1)
EXTRA=""
for f in mediatek/BT_RAM_CODE_MT7922_1_1_hdr.bin mediatek/BT_RAM_CODE_MT7961_1_2_hdr.bin \
         mediatek/mt7925/BT_RAM_CODE_MT7925_1_1_hdr.bin; do
    src=/lib/firmware/$f
    if [ -f "$src.zst" ]; then zstd -q -d -f "$src.zst" -o "$FW/$f"
    elif [ -f "$src" ]; then cp "$src" "$FW/$f"
    else echo "missing firmware $f" >&2; exit 1; fi
    EXTRA="$EXTRA $f"
done
EXTRA=${EXTRA# }

log "config"
cp Microsoft/config-wsl .config
# Stock config-wsl builds USB, usbip and Bluetooth as modules (CONFIG_USB=m), so btusb/btmtk stay modules too
# (shipped in modules.vhdx). Only the MediaTek part is switched on; firmware is built into the image.
./scripts/config --file .config \
    --set-str LOCALVERSION "-microsoft-standard-WSL2-btmtk" \
    --module BT --enable BT_BREDR --enable BT_LE --module BT_RFCOMM \
    --module BT_HCIBTUSB --enable BT_HCIBTUSB_MTK --module BT_MTK \
    --enable FW_LOADER_COMPRESS_ZSTD \
    --set-str EXTRA_FIRMWARE "$EXTRA" --set-str EXTRA_FIRMWARE_DIR "$FW"
make olddefconfig >/dev/null
for s in BT BT_BREDR BT_LE BT_RFCOMM BT_HCIBTUSB BT_HCIBTUSB_MTK BT_MTK; do
    v=$(grep -E "^CONFIG_$s=[ym]" .config || true)
    [ -n "$v" ] || { echo "config check failed for $s" >&2; exit 1; }
done
grep -q '^CONFIG_BT_HCIBTUSB_MTK=y' .config || { echo "BT_HCIBTUSB_MTK not enabled" >&2; exit 1; }

log "build ($JOBS jobs)"
make -j"$JOBS" >/build/make.log 2>&1 || { tail -40 /build/make.log; exit 1; }
RELEASE=$(make -s kernelrelease)

log "modules ($RELEASE)"
rm -rf /build/modules
# stripped: with debug info the modules are ~2 GB, stripped they are about the size of Microsoft's modules.vhd
make INSTALL_MOD_PATH=/build/modules INSTALL_MOD_STRIP=1 modules_install >/build/modules.log 2>&1
rm -f /build/modules.vhdx
# the Microsoft script reads $SUDO_USER under 'set -u'; define it when not running via sudo
SUDO_USER="${SUDO_USER:-}" bash Microsoft/scripts/gen_modules_vhdx.sh /build/modules "$RELEASE" /build/modules.vhdx >/build/vhdx.log 2>&1 \
    || { tail -5 /build/vhdx.log; exit 1; }

log "output"
mkdir -p "$OUT"
cp arch/x86/boot/bzImage "$OUT/bzImage"
cp /build/modules.vhdx "$OUT/modules.vhdx"
cp .config "$OUT/config"
cat > "$OUT/BUILDINFO" <<EOF
release=$RELEASE
source=https://github.com/microsoft/WSL2-Linux-Kernel
tag=$TAG
commit=$COMMIT
patches=$(cd "$HERE/patches" && ls *.patch | tr '\n' ' ')
firmware=$EXTRA
firmware_package=$FWPKG
built=$(date -u +%Y-%m-%dT%H:%M:%SZ)
EOF
( cd "$OUT" && sha256sum bzImage modules.vhdx config > SHA256SUMS )
cat "$OUT/BUILDINFO"
log "done"
