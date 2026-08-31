"""Shared polling for every VisionMesh entity.

One coordinator fetches all camera state on a single timer. Without it, twenty cameras with six
entities each would mean 120 independent pollers hitting the same endpoint, which is how an
integration ends up being the reason someone's Home Assistant feels slow.
"""

from __future__ import annotations

from datetime import timedelta
import logging
from typing import Any

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import ConfigEntryAuthFailed
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator, UpdateFailed

from .api import VisionMeshAuthError, VisionMeshClient, VisionMeshError
from .const import DOMAIN, UPDATE_INTERVAL_SECONDS

_LOGGER = logging.getLogger(__name__)


class VisionMeshCoordinator(DataUpdateCoordinator[dict[str, Any]]):
    """Polls VisionMesh and shares the result with every entity."""

    def __init__(self, hass: HomeAssistant, client: VisionMeshClient, entry: ConfigEntry) -> None:
        super().__init__(
            hass,
            _LOGGER,
            name=DOMAIN,
            update_interval=timedelta(seconds=UPDATE_INTERVAL_SECONDS),
        )
        self.client = client
        self.entry = entry
        self.server_name: str = entry.title
        self.server_version: str = ""

        # Motion is derived rather than reported: VisionMesh raises a motion *event*, and Home
        # Assistant wants a binary state that stays on for a while after it. Tracking the last
        # event id per camera is what turns one into the other without re-reading history.
        self._last_motion_event: dict[str, int] = {}
        self.motion_active: dict[str, bool] = {}

    async def _async_update_data(self) -> dict[str, Any]:
        try:
            cameras = await self.client.async_get_cameras()
            system = await self.client.async_get_system()
            await self._async_update_motion(cameras)

        except VisionMeshAuthError as error:
            # Raising this specific error is what makes Home Assistant offer the re-authentication
            # flow rather than just marking everything unavailable.
            raise ConfigEntryAuthFailed(str(error)) from error
        except VisionMeshError as error:
            raise UpdateFailed(str(error)) from error

        self.server_name = system.get("serverName") or self.entry.title
        self.server_version = system.get("version") or ""

        return {
            "cameras": {camera["id"]: camera for camera in cameras},
            "system": system,
        }

    async def _async_update_motion(self, cameras: list[dict[str, Any]]) -> None:
        """Turns recent motion events into a per-camera boolean."""
        motion_cameras = [c for c in cameras if c.get("supports", {}).get("motion")]
        if not motion_cameras:
            self.motion_active = {}
            return

        try:
            events = await self.client.async_get_recent_events(limit=40)
        except VisionMeshError:
            # Losing the event list is not worth failing the whole update over; motion simply
            # holds its last value until the next poll.
            return

        latest: dict[str, int] = {}
        for event in events:
            if event.get("type") != "Motion":
                continue
            camera_id = event.get("cameraId")
            if camera_id:
                latest[camera_id] = max(latest.get(camera_id, 0), int(event.get("id", 0)))

        active: dict[str, bool] = {}
        for camera in motion_cameras:
            camera_id = camera["id"]
            newest = latest.get(camera_id, 0)
            previous = self._last_motion_event.get(camera_id, 0)

            # Motion is "on" while VisionMesh is recording it, which is the same window the
            # server itself treats as an ongoing motion episode. That keeps the binary sensor in
            # step with what actually gets recorded, instead of inventing a separate timeout here.
            recording = bool((camera.get("health") or {}).get("recording"))
            active[camera_id] = recording and newest > 0

            if newest > previous:
                self._last_motion_event[camera_id] = newest
                active[camera_id] = True

        self.motion_active = active

    def camera(self, camera_id: str) -> dict[str, Any]:
        """Current state for one camera, or an empty dict if it has gone away."""
        return (self.data or {}).get("cameras", {}).get(camera_id, {})

    @property
    def cameras(self) -> dict[str, dict[str, Any]]:
        return (self.data or {}).get("cameras", {})

    @property
    def system(self) -> dict[str, Any]:
        return (self.data or {}).get("system", {})
