"""Diagnostics download.

Redacts anything that could identify the installation or let someone into it, because these files
routinely get attached to public bug reports.
"""

from __future__ import annotations

from typing import Any

from homeassistant.components.diagnostics import async_redact_data
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import CONF_PASSWORD, CONF_URL, CONF_USERNAME
from homeassistant.core import HomeAssistant

from .const import DOMAIN
from .coordinator import VisionMeshCoordinator

TO_REDACT = {CONF_PASSWORD, CONF_USERNAME, CONF_URL, "streamUrl", "snapshotUrl", "path", "token"}


async def async_get_config_entry_diagnostics(hass: HomeAssistant, entry: ConfigEntry) -> dict[str, Any]:
    coordinator: VisionMeshCoordinator = hass.data[DOMAIN][entry.entry_id]

    return {
        "entry": async_redact_data(dict(entry.data), TO_REDACT),
        "options": dict(entry.options),
        "server": async_redact_data(coordinator.system, TO_REDACT),
        "camera_count": len(coordinator.cameras),
        "cameras": [
            async_redact_data(camera, TO_REDACT)
            for camera in coordinator.cameras.values()
        ],
        "motion_active": coordinator.motion_active,
    }
