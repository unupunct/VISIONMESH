"""The payloads the real VisionMesh server returns.

Kept free of any Home Assistant import on purpose: `test_payload_shapes` checks these against the
C# endpoint that produces them, and that check is worth being able to run anywhere, including on
a machine where Home Assistant cannot be installed at all.

Shapes come from `server/VisionMesh.Api/Endpoints/HomeAssistantEndpoints.cs`.
"""

from __future__ import annotations

from typing import Any

SERVER_URL = "http://visionmesh.local:8088"


def make_camera(
    camera_id: str = "cam_abc123",
    name: str = "Front Door",
    state: str = "Online",
    ptz: bool = False,
    motion: bool = False,
) -> dict[str, Any]:
    """One camera in the exact shape /api/homeassistant/entities returns."""
    return {
        "id": camera_id,
        "name": name,
        "uniqueId": f"visionmesh_{camera_id}",
        "sourceKind": "AgentCamera",
        "state": state,
        "ptzSupported": ptz,
        "groupName": None,
        "streamUrl": f"{SERVER_URL}/api/cameras/{camera_id}/stream.mjpeg",
        "snapshotUrl": f"{SERVER_URL}/api/cameras/{camera_id}/snapshot.jpg",
        "supports": {
            "snapshot": True,
            "stream": True,
            "ptz": ptz,
            "privacy": True,
            "recording": True,
            "motion": motion,
        },
        "health": {
            "fps": 15.0,
            "bitrateKbps": 2400.0,
            "latencyMs": 40.0,
            "recording": False,
            "privacy": False,
        },
    }


SYSTEM = {
    "serverName": "Home",
    "version": "1.0.1",
    "platform": "Linux (X64)",
}

STORAGE = {
    "totalBytes": 500_000_000_000,
    "usedBytes": 120_000_000_000,
    "freeBytes": 380_000_000_000,
    "recordingsBytes": 90_000_000_000,
}
