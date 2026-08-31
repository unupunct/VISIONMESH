# Watching from outside your home

## Do not forward a port

The usual advice for reaching something at home is to forward a port on your router. Do not do
that with cameras.

A forwarded port puts your surveillance system on the public internet, where automated scanners
find it within hours. Exposed cameras are one of the most common sources of leaked home footage,
and entire websites exist that do nothing but index them.

VisionMesh is designed so you never need to.

## Use a private network instead

```
        Your home                             Anywhere else
   ┌──────────────────┐                    ┌──────────────────┐
   │ VisionMesh       │                    │ Your phone       │
   │ 100.x.x.x        │◄──── encrypted ───►│ 100.x.x.x        │
   └──────────────────┘   direct, private  └──────────────────┘
                          no open ports
```

Your phone joins the same private network as the server. From the phone's point of view it is at
home, so VisionMesh works exactly as it does on your Wi-Fi, and nothing is exposed to the
internet.

[Tailscale](https://tailscale.com/) is the easiest way to do this. It is free for personal use,
needs no router configuration, and works behind carrier-grade NAT.

## Tailscale, step by step

### Step 1 — Install it on the server

```bash
curl -fsSL https://tailscale.com/install.sh | sh
```

```bash
sudo tailscale up
```

It prints a link. Open it and sign in.

On Windows, download the installer from tailscale.com and sign in.

### Step 2 — Find the server's Tailscale address

```bash
tailscale ip -4
```

**Expected result:** something like `100.101.102.103`.

### Step 3 — Install Tailscale on your phone

From the App Store or Play Store. Sign in with the **same account**.

### Step 4 — Open VisionMesh

On the phone, go to `http://100.101.102.103:8088`.

**Expected result:** the dashboard, with live cameras, over mobile data, from anywhere.

## Getting HTTPS at the same time

This is worth doing, and Tailscale makes it easy.

```bash
sudo tailscale cert $(tailscale status --json | grep -m1 '"DNSName"' | cut -d'"' -f4 | sed 's/\.$//')
```

Or simply enable HTTPS in the Tailscale admin console under DNS, then use the `*.ts.net` name
instead of the numeric address.

Two reasons this matters:

1. Without it, your video and session cookie cross the network unencrypted. Tailscale encrypts the
   tunnel itself, so this is mostly about traffic on your own LAN — but it is still worth having.
2. **Using a phone as a camera requires it.** Browsers only allow a page to use the camera over a
   secure connection. A `*.ts.net` name with a real certificate is the simplest way to get one on
   a home network.

## WireGuard instead

Tailscale is WireGuard with the key exchange handled for you. If you would rather run it yourself,
plain WireGuard works just as well and VisionMesh needs no special configuration for it — set up
the tunnel and use the server's address on that network.

That does mean managing keys and, usually, one forwarded UDP port for the WireGuard endpoint
itself. That single port is a far smaller exposure than exposing the dashboard, since WireGuard
does not respond at all to traffic without a valid key.

## Common problems

**The dashboard will not open over Tailscale.** Check both devices are connected: `tailscale
status` on the server, and the app on the phone. Check you are using the Tailscale address, not
the home one.

**It works on Wi-Fi but not on mobile data.** The phone is probably reaching the server on its
home address, which is cached. Use the Tailscale address explicitly, or enable MagicDNS in the
Tailscale admin console and use the machine name.

**The camera page says it needs a secure connection.** Enable HTTPS on your tailnet and use the
`*.ts.net` name rather than the numeric address.

**Video is choppy over mobile data.** Expected on a weak connection. Lower the camera's frame rate
or resolution — a wall of 1080p cameras is a lot to ask of a mobile link.

## Advanced

VisionMesh does not hard-code any VPN address range. It reports what the operating system says
about each interface, and the Network page marks anything that looks like a tunnel. Ranges differ
per provider and change, so matching on them would be wrong sooner or later.

The Network page lists every address the server can be reached on, including the Tailscale one,
with the address a device on the same LAN should use marked as recommended.

### Behind a reverse proxy

If you put VisionMesh behind Caddy, nginx or Traefik, it honours `X-Forwarded-For` and
`X-Forwarded-Proto`, so the audit log records real client addresses and the session cookie is
marked secure when the outer connection is HTTPS.

Two things the proxy must do:

- **Not buffer responses.** The live stream is a long-lived multipart response, and a buffering
  proxy will hold it. VisionMesh sends `X-Accel-Buffering: no`, which nginx honours.
- **Pass WebSocket upgrades through**, at `/api/ws` and `/agent/ws`.

A minimal Caddy configuration needs neither:

```
visionmesh.example.com {
    reverse_proxy localhost:8088
}
```

Caddy handles WebSocket upgrades and does not buffer by default, and it obtains a certificate
automatically.
