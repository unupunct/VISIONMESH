# Contributing to VisionMesh

Thank you for wanting to help.

## Reporting a bug

The most useful bug report contains three things: what you expected, what happened, and enough
detail to reproduce it.

For a camera problem, open the camera in the dashboard and press **Fix camera**. It checks the
whole chain and reports the first real fault. Paste that output into the issue — it answers most
of the questions a maintainer would otherwise have to ask.

Please also include:

- What VisionMesh version, from the bottom of the sidebar
- What operating system the server runs on
- What kind of camera, and how it is connected
- Whether ffmpeg is installed, from Settings → Advanced

Redact any camera passwords or addresses you would rather not publish. VisionMesh redacts them in
its own logs, but it cannot redact what you paste.

## Building

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Nothing else.

```bash
git clone https://github.com/unupunct/VISIONMESH.git
cd VISIONMESH
dotnet build
dotnet test
dotnet run --project server/VisionMesh.Server
```

The dashboard has no build step. Edit the files in `web/dashboard` and reload the page.

### Verification scripts

Some components are verified against independent implementations rather than against themselves.
These need Node and Python, and are not required to build:

```bash
bash scripts/check-js.sh
```

```bash
node scripts/verify-frame-header.mjs
```

```bash
node scripts/verify-qr.mjs > qr.json && python scripts/verify-qr.py qr.json
```

```bash
dotnet test --filter FullyQualifiedName~EmitJpegSamples && python scripts/verify-jpeg.py
```

## What the code values

VisionMesh has a house style, and it is mostly about honesty.

**No invented data.** If a value has not been measured, it is null and the interface shows a dash,
never a plausible-looking placeholder. A frame rate before enough frames have arrived is unknown,
not zero. Storage projections come from what this installation has actually written, and are
absent until there is enough history to measure.

**Say what is wrong, in words.** Error messages are read by people who did not write the software.
"The camera is already being used by another program" is worth ten of "HRESULT 0xC00D3704".

**Explain the trade, not the mechanism.** Comments should say why a decision was made and what it
costs, not restate the code. A comment explaining why the frame bus drops frames instead of
buffering them is worth keeping; one that says "increment the counter" is not.

**Small dependency surface, especially where bytes from the network are parsed.** The JPEG
decoder, JPEG encoder, QR encoder and ONVIF client are written in-repo on purpose.

**Features that cannot work are switched off and labelled**, not left to fail when pressed.

## Pull requests

- Branch from `main`.
- Run `dotnet test` before opening the PR.
- Add a test for anything that could regress silently. Wire formats, parsers and struct layouts
  especially: those fail by producing nothing rather than by throwing.
- Keep the changelog updated under `## [Unreleased]`.
- One logical change per PR.

## Adding support for a camera

If VisionMesh does not work with a camera you own, that is a bug worth reporting even if you
cannot fix it. Include the manufacturer and model, and:

- For a network camera: the output of Add camera → IP camera, and its ONVIF probe result
- For a USB camera: the output of `VisionMesh.Agent list`

Cameras deviate from their own specifications constantly, and a report from someone who owns one
is the only way most of those deviations get found.
