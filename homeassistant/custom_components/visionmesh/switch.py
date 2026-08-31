"""Switches: privacy mode and recording.

Both write straight through to the server and then refresh, so the switch reflects what actually
happened rather than what was asked for.
"""

from __future__ import annotations

import logging
from typing import Any

from homeassistant.components.switch import SwitchEntity
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .api import VisionMeshError
from .const import DOMAIN
from .coordinator import VisionMeshCoordinator
from .entity import VisionMeshCameraEntity

_LOGGER = logging.getLogger(__name__)


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback) -> None:
    coordinator: VisionMeshCoordinator = hass.data[DOMAIN][entry.entry_id]
    selected = entry.options.get("cameras")

    entities: list[SwitchEntity] = []
    for camera_id, camera in coordinator.cameras.items():
        if selected is not None and camera_id not in selected:
            continue

        entities.append(VisionMeshPrivacySwitch(coordinator, camera_id))
        if camera.get("supports", {}).get("recording"):
            entities.append(VisionMeshRecordingSwitch(coordinator, camera_id))

    async_add_entities(entities)


class VisionMeshPrivacySwitch(VisionMeshCameraEntity, SwitchEntity):
    """Privacy mode: genuinely stops capture and recording, not just the picture."""

    _attr_name = "Privacy mode"
    _attr_icon = "mdi:eye-off"

    def __init__(self, coordinator: VisionMeshCoordinator, camera_id: str) -> None:
        super().__init__(coordinator, camera_id, "privacy")

    @property
    def is_on(self) -> bool:
        return self._camera.get("state") == "Privacy"

    async def async_turn_on(self, **kwargs: Any) -> None:
        await self._set(True)

    async def async_turn_off(self, **kwargs: Any) -> None:
        await self._set(False)

    async def _set(self, enabled: bool) -> None:
        try:
            await self.coordinator.client.async_set_privacy(self._camera_id, enabled)
        except VisionMeshError as error:
            raise HomeAssistantError(f"VisionMesh could not change privacy mode: {error}") from error

        await self.coordinator.async_request_refresh()


class VisionMeshRecordingSwitch(VisionMeshCameraEntity, SwitchEntity):
    """Starts and stops recording on demand."""

    _attr_name = "Record"
    _attr_icon = "mdi:record-rec"

    def __init__(self, coordinator: VisionMeshCoordinator, camera_id: str) -> None:
        super().__init__(coordinator, camera_id, "record")

    @property
    def is_on(self) -> bool:
        return bool((self._camera.get("health") or {}).get("recording"))

    async def async_turn_on(self, **kwargs: Any) -> None:
        await self._set(True)

    async def async_turn_off(self, **kwargs: Any) -> None:
        await self._set(False)

    async def _set(self, start: bool) -> None:
        try:
            await self.coordinator.client.async_set_recording(self._camera_id, start)
        except VisionMeshError as error:
            # The most common cause is ffmpeg missing on the server, and the server says so in
            # its own words, so passing the message straight through is more useful than a generic one.
            raise HomeAssistantError(f"VisionMesh could not change recording: {error}") from error

        await self.coordinator.async_request_refresh()
