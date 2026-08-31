# Security and privacy

VisionMesh handles footage from inside your home. This page says what it does with it, what it
will never do, and where its protection stops.

For reporting a vulnerability, see [SECURITY.md](../SECURITY.md).

## Your footage stays yours

There is no cloud. No account to create. No telemetry. Nothing phones home.

Video never leaves your network unless you deliberately send it somewhere. The server talks to
your cameras, your browser and your Home Assistant, and to nothing else.

You can verify that: the source is here, and a packet capture on the server will show you exactly
what it talks to.

## Privacy mode

Every camera has a privacy mode, and it is not cosmetic. While it is on:

- The camera is not capturing
- Nothing is being recorded
- Anyone opening it is told why there is no picture, rather than seeing a blank rectangle

It overrides everything, including recording schedules. That is the entire point of the feature.

## What VisionMesh will never do

- Hide that a camera is active. If a camera is capturing, the dashboard says so.
- Bypass an operating system's camera indicator, permission prompt or privacy control. The agents
  open cameras exactly the way the operating system intends, which is why the light behaves
  normally.
- Record without that being visible in the interface.
- Claim to detect people. Motion detection detects movement. There is no recognition model, and
  saying otherwise would be a lie the events list would then repeat forever.

## Accounts and roles

| Role | Can |
|---|---|
| **Viewer** | Watch cameras and see events |
| **Operator** | Also record, pause, move cameras and set privacy mode |
| **Administrator** | Everything: cameras, users, storage, integrations, settings |

Give people the smallest role that lets them do what they need. Make a separate account for Home
Assistant rather than reusing your own — it makes the audit log readable and can be revoked on its
own.

The last active administrator cannot be deleted, disabled or demoted, so the server cannot be
locked out of.

## How credentials are stored

**Your password** is hashed with PBKDF2-HMAC-SHA256, a random per-hash salt and 210,000
iterations. It is never recoverable and never logged. There is no password reset, by design: a
surveillance server with a reset backdoor is one that anyone with local access can take over.

**Camera passwords** are encrypted with AES-256-GCM, keyed from a file beside the database with
owner-only permissions. They are never returned by the API, in any form. Any URL that reaches a
log, an error message or the camera panel has its credentials replaced with `***` first.

**Device tokens** are stored only as hashes. A copy of the database does not let anyone
impersonate a camera.

**Pairing codes** are single-use, expire in ten minutes, and are consumed atomically, so two
devices racing on one code cannot both win. A QR code never contains a permanent credential — only
the short-lived code and the address to redeem it at.

## The audit log

Settings → Security log records sign-ins, failed sign-ins, camera changes, pairing, privacy mode
changes and PTZ commands, with who did it and from where.

It is visible to administrators only.

## Where the protection stops

Being clear about this is part of being trustworthy.

**Plain HTTP.** By default the server speaks HTTP. On your home network, anyone who can see your
traffic can see your video and your session cookie. Put VisionMesh behind a reverse proxy with a
certificate, or reach it only over a private network such as
[Tailscale](11-tailscale.md). This is the single most valuable thing you can do.

**Local file access.** The credential encryption key sits beside the database. It protects a
database copied off the machine; it cannot protect against someone who already has the server's
own file access. No self-hosted design can, without external key management, and pretending
otherwise would be worse than saying so.

**Exposing the server to the internet.** Do not forward a port to it. Cameras exposed that way are
found and probed within hours.

**A compromised agent machine.** An agent's token lets it publish frames for the cameras bound to
it. Treat machines running agents as part of your trusted network.

## A checklist

- [ ] A strong administrator password. Length beats complexity — a phrase you remember is better
      than a scrambled word you do not.
- [ ] HTTPS, via a reverse proxy or Tailscale.
- [ ] No port forwarded to VisionMesh.
- [ ] Cameras' own default passwords changed. This matters more than anything VisionMesh does: a
      camera with a default password is compromised regardless of what sits in front of it.
- [ ] Viewer accounts for people who only watch.
- [ ] A separate account for Home Assistant.
- [ ] `secret.key` backed up somewhere safe.
- [ ] Cameras not pointed anywhere you would not want a recording of.

That last one is not a joke. The most common privacy failure in a home surveillance system is not
a technical breach; it is a camera pointed at a bedroom door, or a neighbour's window, by someone
who did not think about it at install time.

## Advanced

### Why sessions are not JWTs

Sessions are opaque random tokens, stored hashed, checked against the database on every request.
For a single-server self-hosted product, immediate revocation is worth more than stateless
validation. Changing a password or reducing a role drops every existing session for that account
at once, which a JWT cannot do without a revocation list that defeats the point.

### Stream tokens

An `<img>` tag cannot send an `Authorization` header, and putting a session token in a URL would
put it in browser history, proxy logs and screenshots.

The dashboard therefore relies on the session cookie, which the browser attaches automatically.
Clients that have no cookie — Home Assistant, a native player — request a token scoped to **one
camera** and valid for **two minutes**. A token that leaks grants very little, for very long.

### Login throttling

Five free attempts per username, then a delay that doubles up to five minutes. Failed logins are
verified against a real dummy hash, so response time does not reveal which usernames exist.

The throttle is per username and held in memory: this is abuse resistance for a LAN service, not
an audit trail, and it should reset when the server restarts.

### The parsing surface

The most exposed code in any surveillance system is whatever parses bytes arriving from cameras.
VisionMesh's JPEG decoder, JPEG encoder, QR encoder and ONVIF client are written in-repo rather
than pulled from packages, because those are exactly the components with a long history of parser
vulnerabilities.

The JPEG decoder treats every malformed input as "not decodable" rather than throwing, never
allocates based on an unvalidated size field, and is fuzzed in the test suite with hundreds of
randomly corrupted frames.

### Auditing dependencies

```bash
dotnet list package --vulnerable --include-transitive
```
