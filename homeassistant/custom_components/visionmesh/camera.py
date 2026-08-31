"""Camera entities.

Video comes straight from the VisionMesh server to whatever is displaying it, rather than being
proxied through this integration. Home Assistant only supplies the URL and the still image.
"""

from __future__ import annotations

import logging
from typing import Any

from homeassistant.components.camera import Camera, CameraEntityFeature
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .const import ATTR_CAMERA_ID, ATTR_GROUP, ATTR_SOURCE_KIND, DOMAIN
from .coordinator import VisionMeshCoordinator
from .entity import VisionMeshCameraEntity

_LOGGER = logging.getLogger(__name__)


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback) -> None:
    coordinator: VisionMeshCoordinator = hass.data[DOMAIN][entry.entry_id]
    selected = entry.options.get("cameras")

    async_add_entities(
        VisionMeshCamera(coordinator, camera_id)
        for camera_id in coordinator.cameras
        if selected is None or camera_id in selected
    )


class VisionMeshCamera(VisionMeshCameraEntity, Camera):
    """One VisionMesh camera."""

    _attr_name = None   # the device name is the camera name, so the entity needs no suffix
    _attr_supported_features = CameraEntityFeature.STREAM

    def __init__(self, coordinator: VisionMeshCoordinator, camera_id: str) -> None:
        VisionMeshCameraEntity.__init__(self, coordinator, camera_id, "camera")
        Camera.__init__(self)

    @property
    def is_on(self) -> bool:
        """False in privacy mode, which is what makes the entity visibly stop."""
        camera = self._camera
        return camera.get("state") != "Privacy" and camera.get("state") != "Paused"

    @property
    def is_recording(self) -> bool:
        return bool((self._camera.get("health") or {}).get("recording"))

    @property
    def motion_detection_enabled(self) -> bool:
        return bool(self._camera.get("supports", {}).get("motion"))

    @property
    def brand(self) -> str:
        return "VisionMesh"

    @property
    def model(self) -> str | None:
        return self._camera.get("sourceKind")

    async def async_camera_image(self, width: int | None = None, height: int | None = None) -> bytes | None:
        """Still image. Returns None in privacy mode, so nothing is shown rather than a stale frame."""
        if not self.is_on:
            return None
        return await self.coordinator.client.async_get_snapshot(self._camera_id)

    async def stream_source(self) -> str | None:
        """
        Live stream URL.

        A short-lived, camera-scoped token is minted for each request rather than embedding the
        session token, so the URL that ends up in a browser or a cast device grants access to one
        camera for a couple of minutes and nothing more.
        """
        if not self.is_on:
            return None

        token = await self.coordinator.client.async_create_stream_token(self._camera_id)
        if token is None:
            return None

        return self.coordinator.client.stream_url(self._camera_id, token)

    @property
    def extra_state_attributes(self) -> dict[str, Any]:
        camera = self._camera
        health = camera.get("health") or {}

        # Only real measurements are published. A value the server has not measured yet is absent
        # rather than zero, so a template cannot mistake "not known" for "nothing happening".
        attributes: dict[str, Any] = {
            ATTR_CAMERA_ID: self._camera_id,
            ATTR_SOURCE_KIND: camera.get("sourceKind"),
            "state": camera.get("state"),
        }

        if camera.get("groupName"):
            attributes[ATTR_GROUP] = camera["groupName"]
        if health.get("fps") is not None:
            attributes["fps"] = health["fps"]
        if health.get("bitrateBps") is not None:
            attributes["bitrate_kbps"] = round(health["bitrateBps"] / 1000, 1)
        if health.get("width"):
            attributes["resolution"] = f"{health['width']}x{health['height']}"
        if health.get("batteryPercent") is not None:
            attributes["battery"] = health["batteryPercent"]

        return attributes
