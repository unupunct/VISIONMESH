"""Buttons: pan, tilt and zoom.

Only created for cameras that actually report PTZ support, so no camera gets controls it cannot
obey. Each press nudges the camera and stops it again, which is what a button can express - the
dashboard offers hold-to-move for finer control.
"""

from __future__ import annotations

import asyncio
import logging

from homeassistant.components.button import ButtonEntity
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .api import VisionMeshError
from .const import DOMAIN
from .coordinator import VisionMeshCoordinator
from .entity import VisionMeshCameraEntity

_LOGGER = logging.getLogger(__name__)

# How long one press moves the camera for. Long enough to see, short enough not to swing past
# whatever the user was aiming at.
NUDGE_SECONDS = 0.4
NUDGE_SPEED = 0.5

DIRECTIONS = (
    ("left", "Pan left", "mdi:arrow-left", -NUDGE_SPEED, 0.0, 0.0),
    ("right", "Pan right", "mdi:arrow-right", NUDGE_SPEED, 0.0, 0.0),
    ("up", "Tilt up", "mdi:arrow-up", 0.0, NUDGE_SPEED, 0.0),
    ("down", "Tilt down", "mdi:arrow-down", 0.0, -NUDGE_SPEED, 0.0),
    ("zoom_in", "Zoom in", "mdi:magnify-plus", 0.0, 0.0, NUDGE_SPEED),
    ("zoom_out", "Zoom out", "mdi:magnify-minus", 0.0, 0.0, -NUDGE_SPEED),
)


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback) -> None:
    coordinator: VisionMeshCoordinator = hass.data[DOMAIN][entry.entry_id]
    selected = entry.options.get("cameras")

    entities: list[ButtonEntity] = []
    for camera_id, camera in coordinator.cameras.items():
        if selected is not None and camera_id not in selected:
            continue
        if not camera.get("ptzSupported"):
            continue

        entities.extend(
            VisionMeshPtzButton(coordinator, camera_id, key, name, icon, pan, tilt, zoom)
            for key, name, icon, pan, tilt, zoom in DIRECTIONS
        )

    async_add_entities(entities)


class VisionMeshPtzButton(VisionMeshCameraEntity, ButtonEntity):
    """Moves a PTZ camera a short distance in one direction."""

    def __init__(
        self,
        coordinator: VisionMeshCoordinator,
        camera_id: str,
        key: str,
        name: str,
        icon: str,
        pan: float,
        tilt: float,
        zoom: float,
    ) -> None:
        super().__init__(coordinator, camera_id, f"ptz_{key}")
        self._attr_name = name
        self._attr_icon = icon
        self._pan = pan
        self._tilt = tilt
        self._zoom = zoom

    async def async_press(self) -> None:
        client = self.coordinator.client

        try:
            await client.async_ptz(self._camera_id, pan=self._pan, tilt=self._tilt, zoom=self._zoom)
            await asyncio.sleep(NUDGE_SECONDS)
        except VisionMeshError as error:
            raise HomeAssistantError(f"VisionMesh could not move the camera: {error}") from error
        finally:
            # The stop has to happen even if the move failed part way, or a camera that accepted
            # the first command would keep turning until it hit its limit.
            try:
                await client.async_ptz(self._camera_id, stop=True)
            except VisionMeshError as error:
                _LOGGER.warning("Could not stop camera %s after a move: %s", self._camera_id, error)
