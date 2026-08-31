"""VisionMesh integration for Home Assistant.

Brings the cameras from a self-hosted VisionMesh server into Home Assistant as camera entities
with live video, plus the sensors and switches needed to build automations around them.
"""

from __future__ import annotations

import logging

from homeassistant.config_entries import ConfigEntry
from homeassistant.const import CONF_PASSWORD, CONF_URL, CONF_USERNAME, Platform
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import ConfigEntryAuthFailed, ConfigEntryNotReady
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .api import VisionMeshAuthError, VisionMeshClient, VisionMeshError
from .const import CONF_VERIFY_SSL, DOMAIN
from .coordinator import VisionMeshCoordinator

_LOGGER = logging.getLogger(__name__)

PLATFORMS: list[Platform] = [
    Platform.CAMERA,
    Platform.BINARY_SENSOR,
    Platform.SENSOR,
    Platform.SWITCH,
    Platform.BUTTON,
]


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Sets up one VisionMesh server."""
    session = async_get_clientsession(hass)

    client = VisionMeshClient(
        session,
        entry.data[CONF_URL],
        entry.data[CONF_USERNAME],
        entry.data[CONF_PASSWORD],
        entry.data.get(CONF_VERIFY_SSL, True),
    )

    try:
        await client.async_login()
    except VisionMeshAuthError as error:
        # Prompts the user to re-enter credentials rather than leaving the integration broken.
        raise ConfigEntryAuthFailed(str(error)) from error
    except VisionMeshError as error:
        # Home Assistant retries this automatically, which is what should happen when the
        # VisionMesh server is simply still starting up.
        raise ConfigEntryNotReady(str(error)) from error

    coordinator = VisionMeshCoordinator(hass, client, entry)
    await coordinator.async_config_entry_first_refresh()

    hass.data.setdefault(DOMAIN, {})[entry.entry_id] = coordinator

    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    entry.async_on_unload(entry.add_update_listener(_async_reload_on_options_change))

    _LOGGER.info(
        "Connected to VisionMesh '%s' with %d camera(s).",
        coordinator.server_name,
        len(coordinator.cameras),
    )
    return True


async def async_unload_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Removes one VisionMesh server."""
    unloaded = await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
    if unloaded:
        hass.data[DOMAIN].pop(entry.entry_id, None)
    return unloaded


async def _async_reload_on_options_change(hass: HomeAssistant, entry: ConfigEntry) -> None:
    """Reloads when the user changes which cameras are exposed."""
    await hass.config_entries.async_reload(entry.entry_id)
