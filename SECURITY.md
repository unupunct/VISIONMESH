# Security

VisionMesh handles camera footage from inside people's homes. That deserves care, and it deserves
honesty about what the software does and does not protect against.

## Reporting a vulnerability

Please report security issues privately, through
[GitHub security advisories](https://github.com/unupunct/VISIONMESH/security/advisories/new),
rather than as a public issue.

Include what you found, how to reproduce it, and what an attacker could do with it. You will get
an acknowledgement within a few days. Please give a reasonable window for a fix before disclosing
publicly.

## What VisionMesh does

**Passwords.** Hashed with PBKDF2-HMAC-SHA256, a per-hash random salt and 210,000 iterations. The
iteration count travels with the hash, so it can be raised later without invalidating existing
passwords. Passwords are never recoverable, and never logged.

**Camera credentials.** RTSP and ONVIF passwords are encrypted at rest with AES-256-GCM, keyed
from a file created beside the database with owner-only permissions. They are never returned by
the API in any form, and the URLs that carry them are redacted before they reach a log, an error
message or a diagnostics panel.

**Device tokens.** Stored only as SHA-256 hashes. A copy of the database does not let anyone
impersonate a camera.

**Pairing.** Codes are single-use, expire in ten minutes, and are consumed atomically so two
devices racing on one code cannot both win. A QR code never contains a permanent credential.

**Sessions.** Opaque random tokens, stored hashed, revocable immediately. Not JWTs: for a
single-server self-hosted product, immediate revocation is worth more than stateless validation.
Changing a password or reducing a role drops every existing session for that account.

**Stream tokens.** Clients that cannot set an `Authorization` header on a media request get a
token scoped to one camera and valid for two minutes, rather than a session token in a URL.

**Login throttling.** Five free attempts per username, then an exponential delay up to five
minutes. Failed logins are verified against a dummy hash so response time does not reveal which
usernames exist.

**Roles.** Viewer can watch. Operator can also record, pause, move and set privacy mode.
Administrator can change everything. The last active administrator cannot be deleted, disabled or
demoted.

**Stream URLs.** RTSP addresses are validated against an allowlist of network media protocols
before they are handed to ffmpeg, which otherwise also speaks `file:` and `concat:`.

**Recording playback.** Paths are resolved and confined to the recordings folder, so a corrupted
index row cannot serve an arbitrary file.

**Untrusted input.** Frames arriving from cameras are parsed by VisionMesh's own JPEG reader,
which treats every malformed input as "not decodable" rather than throwing, is fuzzed in the test
suite, and never allocates based on an unvalidated size field. No third-party image decoder is in
that path.

## What VisionMesh will never do

- Hide that a camera is active, or record without that being visible in the interface.
- Bypass an operating system's camera indicator, permission prompt or privacy control.
- Send footage anywhere off your network. There is no cloud, no telemetry, and nothing phones home.
- Claim to detect people. Motion detection detects movement. There is no recognition model.

Privacy mode is not cosmetic: it stops capture and recording, and anyone who opens the camera is
told why there is no picture.

## What VisionMesh does not protect against

Being clear about the limits is part of being trustworthy.

- **Plain HTTP.** By default the server speaks HTTP. On a home network, anyone who can see your
  traffic can see your video and your session cookie. Put it behind a reverse proxy with a
  certificate, or reach it only over a private network such as Tailscale or WireGuard.
- **Local file access.** The credential encryption key sits beside the database. It protects a
  database that is copied off the machine; it cannot protect against someone who already has the
  server's own file access. No self-hosted design can, without external key management.
- **Exposing the server to the internet.** Do not forward a port to it. Cameras exposed that way
  are found and probed within hours.
- **A compromised agent machine.** An agent's token grants the ability to publish frames for the
  cameras bound to it. Treat the machines running agents as part of your trusted network.

## Dependencies

VisionMesh deliberately keeps a small dependency surface, especially in code that parses bytes
from the network. Its JPEG decoder, JPEG encoder, QR encoder and ONVIF client are written in-repo
rather than pulled in, because those are exactly the components with a history of parser
vulnerabilities.

Run `dotnet list package --vulnerable --include-transitive` to audit what is left.

## Supported versions

| Version | Supported |
|---|---|
| 1.0.x | Yes |
