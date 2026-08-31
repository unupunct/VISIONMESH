#!/usr/bin/env bash
#
# VisionMesh installer for Linux.
#
# Installs or upgrades the VisionMesh server as a systemd service. Running it twice is safe: an
# upgrade replaces the program and leaves the database, recordings and settings alone. That
# matters more than it sounds - an installer that quietly resets someone's surveillance system
# because they ran it again is worse than one that refuses to run at all.
#
#   sudo ./install.sh                     install or upgrade
#   sudo ./install.sh --version v1.0.0    install a specific version
#   sudo ./install.sh --port 9000         listen on a different port
#   sudo ./install.sh --uninstall         remove the service, keep the data
#
set -euo pipefail

REPO="unupunct/VISIONMESH"
INSTALL_DIR="/opt/visionmesh"
DATA_DIR="/var/lib/visionmesh"
SERVICE_NAME="visionmesh"
SERVICE_USER="visionmesh"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
DEFAULT_PORT=8088

VERSION=""
PORT="$DEFAULT_PORT"
UNINSTALL=0
SKIP_START=0

# ---- output ----------------------------------------------------------------

if [ -t 1 ]; then
    BOLD=$(printf '\033[1m'); DIM=$(printf '\033[2m'); RED=$(printf '\033[31m')
    GREEN=$(printf '\033[32m'); YELLOW=$(printf '\033[33m'); RESET=$(printf '\033[0m')
else
    BOLD=""; DIM=""; RED=""; GREEN=""; YELLOW=""; RESET=""
fi

say()  { printf '%s\n' "$*"; }
step() { printf '%s==>%s %s\n' "$BOLD" "$RESET" "$*"; }
ok()   { printf '  %s✓%s %s\n' "$GREEN" "$RESET" "$*"; }
warn() { printf '  %s!%s %s\n' "$YELLOW" "$RESET" "$*"; }
die()  { printf '%sError:%s %s\n' "$RED" "$RESET" "$*" >&2; exit 1; }

# ---- arguments -------------------------------------------------------------

while [ $# -gt 0 ]; do
    case "$1" in
        --version) VERSION="${2:-}"; shift 2 ;;
        --port)    PORT="${2:-}"; shift 2 ;;
        --uninstall) UNINSTALL=1; shift ;;
        --no-start) SKIP_START=1; shift ;;
        -h|--help)
            sed -n '3,14p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) die "Unknown option: $1. Try --help." ;;
    esac
done

[ "$(id -u)" -eq 0 ] || die "This installer needs root. Run it with sudo."

# ---- uninstall -------------------------------------------------------------

if [ "$UNINSTALL" -eq 1 ]; then
    step "Removing VisionMesh"

    if systemctl list-unit-files | grep -q "^${SERVICE_NAME}.service"; then
        systemctl stop "$SERVICE_NAME" 2>/dev/null || true
        systemctl disable "$SERVICE_NAME" 2>/dev/null || true
        rm -f "$SERVICE_FILE"
        systemctl daemon-reload
        ok "Service removed"
    fi

    rm -rf "$INSTALL_DIR"
    ok "Program removed from $INSTALL_DIR"

    say ""
    say "Your data is still in $DATA_DIR, including recordings and the encryption key."
    say "Delete it yourself if you want it gone:"
    say ""
    say "    sudo rm -rf $DATA_DIR"
    say ""
    exit 0
fi

# ---- detect the machine ----------------------------------------------------

step "Checking this machine"

DISTRO="unknown"; DISTRO_NAME="Linux"
if [ -r /etc/os-release ]; then
    # shellcheck disable=SC1091
    . /etc/os-release
    DISTRO="${ID:-unknown}"
    DISTRO_NAME="${PRETTY_NAME:-$DISTRO}"
fi
ok "$DISTRO_NAME"

case "$(uname -m)" in
    x86_64|amd64) ARCH="x64" ;;
    aarch64|arm64) ARCH="arm64" ;;
    armv7l|armv6l)
        die "32-bit ARM is not supported. VisionMesh needs a 64-bit system.
     On a Raspberry Pi, install the 64-bit version of Raspberry Pi OS." ;;
    *) die "Unsupported architecture: $(uname -m). VisionMesh supports x86_64 and arm64." ;;
esac
ok "Architecture: $ARCH"

command -v systemctl >/dev/null 2>&1 || die "This installer needs systemd.
     Without it, download the release and run VisionMesh.Server yourself."

# ---- tools -----------------------------------------------------------------

if command -v curl >/dev/null 2>&1; then
    DOWNLOAD="curl -fL --progress-bar -o"
    FETCH="curl -fsSL"
elif command -v wget >/dev/null 2>&1; then
    DOWNLOAD="wget -q --show-progress -O"
    FETCH="wget -qO-"
else
    die "Neither curl nor wget is installed. Install one and try again."
fi

command -v tar >/dev/null 2>&1 || die "tar is not installed. Install it and try again."

# ---- ffmpeg ----------------------------------------------------------------

step "Checking for ffmpeg"
if command -v ffmpeg >/dev/null 2>&1; then
    ok "ffmpeg $(ffmpeg -version 2>/dev/null | head -n1 | awk '{print $3}')"
else
    warn "ffmpeg is not installed."
    say "    VisionMesh needs it for network cameras and for recording."
    say "    USB cameras and phone cameras work without it."
    say ""
    case "$DISTRO" in
        ubuntu|debian|raspbian|linuxmint|pop) say "    Install it with:  sudo apt install ffmpeg" ;;
        fedora|rhel|centos|rocky|almalinux)   say "    Install it with:  sudo dnf install ffmpeg" ;;
        arch|manjaro|endeavouros)             say "    Install it with:  sudo pacman -S ffmpeg" ;;
        opensuse*|sles)                       say "    Install it with:  sudo zypper install ffmpeg" ;;
        alpine)                               say "    Install it with:  sudo apk add ffmpeg" ;;
        *)                                    say "    Install it with your package manager." ;;
    esac
    say ""
fi

# ---- work out which version --------------------------------------------------

step "Finding the release to install"

if [ -z "$VERSION" ]; then
    VERSION=$($FETCH "https://api.github.com/repos/${REPO}/releases/latest" 2>/dev/null \
        | grep -m1 '"tag_name"' | cut -d'"' -f4 || true)

    [ -n "$VERSION" ] || die "Could not work out the latest version.
     Check your internet connection, or pass one explicitly:
         sudo ./install.sh --version v1.0.0"
fi
ok "Version $VERSION"

ASSET="VisionMesh-Server-Linux-${ARCH}.tar.gz"
URL="https://github.com/${REPO}/releases/download/${VERSION}/${ASSET}"

# ---- existing installation --------------------------------------------------

UPGRADE=0
if [ -d "$INSTALL_DIR" ] || [ -f "$SERVICE_FILE" ]; then
    UPGRADE=1
    step "Upgrading the existing installation"
    ok "Your database, recordings and settings are kept"
fi

# ---- download ----------------------------------------------------------------

step "Downloading"

TEMP_DIR=$(mktemp -d)
# Clean up whatever happens, including on a failed download.
trap 'rm -rf "$TEMP_DIR"' EXIT

$DOWNLOAD "${TEMP_DIR}/${ASSET}" "$URL" \
    || die "Could not download $ASSET.
     Check that $VERSION exists at:
         https://github.com/${REPO}/releases"

# Verify against the release checksums when they are published.
if $FETCH "https://github.com/${REPO}/releases/download/${VERSION}/SHA256SUMS.txt" \
        > "${TEMP_DIR}/SHA256SUMS.txt" 2>/dev/null && [ -s "${TEMP_DIR}/SHA256SUMS.txt" ]; then
    if command -v sha256sum >/dev/null 2>&1; then
        EXPECTED=$(grep " ${ASSET}\$" "${TEMP_DIR}/SHA256SUMS.txt" | awk '{print $1}' || true)
        if [ -n "$EXPECTED" ]; then
            ACTUAL=$(sha256sum "${TEMP_DIR}/${ASSET}" | awk '{print $1}')
            [ "$EXPECTED" = "$ACTUAL" ] || die "The downloaded file does not match its published checksum.
     This could mean a corrupted download, or something worse. Nothing has been installed."
            ok "Checksum verified"
        fi
    fi
else
    warn "No checksums published for this release; skipping verification"
fi

# ---- stop the running service -----------------------------------------------

if [ "$UPGRADE" -eq 1 ] && systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
    step "Stopping VisionMesh"
    systemctl stop "$SERVICE_NAME"
    ok "Stopped"
fi

# ---- install ------------------------------------------------------------------

step "Installing to $INSTALL_DIR"

mkdir -p "$INSTALL_DIR"
tar -xzf "${TEMP_DIR}/${ASSET}" -C "$INSTALL_DIR"

# The archive may or may not contain a top-level folder, depending on how it was built.
if [ ! -f "${INSTALL_DIR}/VisionMesh.Server" ]; then
    INNER=$(find "$INSTALL_DIR" -maxdepth 2 -name VisionMesh.Server -type f | head -n1 || true)
    [ -n "$INNER" ] || die "The downloaded archive does not contain VisionMesh.Server."
    INNER_DIR=$(dirname "$INNER")
    if [ "$INNER_DIR" != "$INSTALL_DIR" ]; then
        mv "$INNER_DIR"/* "$INSTALL_DIR"/ 2>/dev/null || true
        rmdir "$INNER_DIR" 2>/dev/null || true
    fi
fi

chmod +x "${INSTALL_DIR}/VisionMesh.Server"
ok "Installed"

# ---- service account ----------------------------------------------------------

step "Setting up the service account"

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --no-create-home --shell /usr/sbin/nologin \
        --comment "VisionMesh service account" "$SERVICE_USER" 2>/dev/null \
        || useradd --system --no-create-home --shell /sbin/nologin "$SERVICE_USER"
    ok "Created user '$SERVICE_USER'"
else
    ok "User '$SERVICE_USER' already exists"
fi

# The video group lets the server use a camera plugged into this machine directly.
if getent group video >/dev/null 2>&1; then
    usermod -aG video "$SERVICE_USER" 2>/dev/null || true
    ok "Added to the 'video' group, so local cameras can be used"
fi

mkdir -p "$DATA_DIR" "${DATA_DIR}/recordings"
chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_DIR"
# The data directory holds the encryption key, so it is not world readable.
chmod 750 "$DATA_DIR"
chown -R root:root "$INSTALL_DIR"
ok "Data directory ready at $DATA_DIR"

# ---- systemd unit --------------------------------------------------------------

step "Installing the service"

cat > "$SERVICE_FILE" <<UNIT
[Unit]
Description=VisionMesh camera and surveillance server
Documentation=https://github.com/${REPO}
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=${SERVICE_USER}
Group=${SERVICE_USER}
ExecStart=${INSTALL_DIR}/VisionMesh.Server --port ${PORT} --data ${DATA_DIR}
WorkingDirectory=${INSTALL_DIR}
Restart=on-failure
RestartSec=5
KillSignal=SIGTERM
TimeoutStopSec=30

# A surveillance server should not be able to do much beyond its own job.
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
ReadWritePaths=${DATA_DIR}
# Cameras arrive over the network and locally, so both address families are needed.
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK
# Video devices have to stay reachable for a camera plugged into this machine.
DeviceAllow=char-video4linux rw
SupplementaryGroups=video

# The dashboard and any recorder subprocesses can open a lot of files at once.
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable "$SERVICE_NAME" >/dev/null 2>&1
ok "Service installed"

# ---- start ---------------------------------------------------------------------

if [ "$SKIP_START" -eq 0 ]; then
    step "Starting VisionMesh"
    systemctl start "$SERVICE_NAME"

    # Give it a moment, then report what actually happened rather than assuming success.
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        sleep 1
        systemctl is-active --quiet "$SERVICE_NAME" && break
    done

    if systemctl is-active --quiet "$SERVICE_NAME"; then
        ok "Running"
    else
        say ""
        die "VisionMesh did not start. See what went wrong with:
         sudo journalctl -u ${SERVICE_NAME} -n 50 --no-pager"
    fi
fi

# ---- where to go next -----------------------------------------------------------

ADDRESS=$(hostname -I 2>/dev/null | awk '{print $1}')
[ -n "$ADDRESS" ] || ADDRESS="localhost"

say ""
say "${BOLD}VisionMesh ${VERSION} is installed and running.${RESET}"
say ""
say "  Open the dashboard at:  ${BOLD}http://${ADDRESS}:${PORT}${RESET}"
say ""
say "  ${DIM}Follow the setup wizard to create your account and add your first camera.${RESET}"
say ""
say "  Status:   sudo systemctl status ${SERVICE_NAME}"
say "  Logs:     sudo journalctl -u ${SERVICE_NAME} -f"
say "  Restart:  sudo systemctl restart ${SERVICE_NAME}"
say ""
say "  Data:     ${DATA_DIR}"
say "  ${DIM}Back this up. Without secret.key, saved camera passwords cannot be decrypted.${RESET}"
say ""
