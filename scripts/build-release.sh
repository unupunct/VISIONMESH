#!/usr/bin/env bash
#
# Builds every VisionMesh release artefact and writes checksums.
#
# Each binary is self-contained and single-file, so a user downloads one thing and runs it. That
# costs about 70 MB per artefact and is worth it: "install the .NET runtime first" is where most
# people give up on a self-hosted project.
#
#   ./scripts/build-release.sh [version]
#
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${1:-1.0.0}"
OUT="artifacts"
STAGE="${OUT}/stage"

COMMON=(
    -c Release
    --self-contained true
    -p:PublishSingleFile=true
    -p:IncludeNativeLibrariesForSelfExtract=true
    -p:EnableCompressionInSingleFile=true
    -p:DebugType=none
    -p:Version="${VERSION}"
    -p:AssemblyVersion="${VERSION}.0"
    -p:FileVersion="${VERSION}.0"
)

say() { printf '\n==> %s\n' "$*"; }

rm -rf "$OUT"
mkdir -p "$OUT" "$STAGE"

# ---- server ----------------------------------------------------------------

for RID in win-x64 linux-x64 linux-arm64; do
    say "Building the server for ${RID}"

    DIR="${STAGE}/server-${RID}"
    dotnet publish server/VisionMesh.Server "${COMMON[@]}" -r "$RID" -o "$DIR"

    # Ship the docs a user is most likely to need beside the binary itself.
    cp README.md LICENSE INSTALL.md "$DIR/" 2>/dev/null || true
    cp -r docs "$DIR/docs" 2>/dev/null || true

    case "$RID" in
        win-x64)
            (cd "$DIR" && zip -qr "../../VisionMesh-Server-Windows-x64.zip" .)
            ;;
        *)
            PLATFORM="Linux-${RID#linux-}"
            tar -czf "${OUT}/VisionMesh-Server-${PLATFORM}.tar.gz" -C "$DIR" .
            ;;
    esac
done

# ---- agents ------------------------------------------------------------------

say "Building the Windows agent"
AGENT_WIN="${STAGE}/agent-win-x64"
dotnet publish agents/windows/VisionMesh.Agent.Windows "${COMMON[@]}" -r win-x64 -o "$AGENT_WIN"
cp LICENSE "$AGENT_WIN/" 2>/dev/null || true
(cd "$AGENT_WIN" && zip -qr "../../VisionMesh-Agent-Windows-x64.zip" .)

for RID in linux-x64 linux-arm64; do
    say "Building the Linux agent for ${RID}"

    DIR="${STAGE}/agent-${RID}"
    dotnet publish agents/linux/VisionMesh.Agent.Linux "${COMMON[@]}" -r "$RID" -o "$DIR"

    # A predictable command name, so the documentation can name one thing.
    mv "${DIR}/VisionMesh.Agent.Linux" "${DIR}/visionmesh-agent"
    chmod +x "${DIR}/visionmesh-agent"
    cp LICENSE scripts/visionmesh-agent.service "$DIR/" 2>/dev/null || true

    PLATFORM="Linux-${RID#linux-}"
    tar -czf "${OUT}/VisionMesh-Agent-${PLATFORM}.tar.gz" -C "$DIR" .
done

# ---- home assistant integration ------------------------------------------------

say "Packaging the Home Assistant integration"
(cd homeassistant && zip -qr "../${OUT}/VisionMesh-HomeAssistant-Integration.zip" custom_components)

# ---- checksums ------------------------------------------------------------------

say "Writing checksums"
rm -rf "$STAGE"

(
    cd "$OUT"
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum ./* > SHA256SUMS.txt
    else
        shasum -a 256 ./* > SHA256SUMS.txt
    fi
    sed -i'' -e 's|\./||' SHA256SUMS.txt 2>/dev/null || true
)

say "Done"
ls -lh "$OUT"
