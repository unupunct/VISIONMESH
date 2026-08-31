"""What actually appears in Home Assistant once a VisionMesh server is added.

The point of these is not that the code runs — it is that the entities a user will build
automations against exist, carry the right state, and survive the server going away.
"""

from __future__ import annotations

from unittest.mock import AsyncMock

from custom_components.visionmesh.api import VisionMeshError

from homeassistant.config_entries import ConfigEntryState
from homeassistant.const import STATE_UNAVAILABLE, Platform
from homeassistant.core import HomeAssistant
from homeassistant.helpers import device_registry as dr, entity_registry as er

from .conftest import DOMAIN, make_camera


async def _setup(hass: HomeAssistant, entry) -> None:
    entry.add_to_hass(hass)
    assert await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()


async def test_the_integration_loads_and_unloads_cleanly(
    hass: HomeAssistant, mock_client: AsyncMock, config_entry
) -> None:
    """An integration that cannot be unloaded cannot be reconfigured or updated in place."""
    await _setup(hass, config_entry)
    assert config_entry.state is ConfigEntryState.LOADED

    assert await hass.config_entries.async_unload(config_entry.entry_id)
    await hass.async_block_till_done()
    assert config_entry.state is ConfigEntryState.NOT_LOADED


async def test_a_camera_produces_the_expected_entities(
    hass: HomeAssistant, mock_client: AsyncMock, config_entry
) -> None:
    await _setup(hass, config_entry)

    registry = er.async_get(hass)
    entities = er.async_entries_for_config_entry(registry, config_entry.entry_id)
    domains = {entity.domain for entity in entities}

    # A camera is not useful in Home Assistant unless it brings video *and* something to automate
    # against.
    assert Platform.CAMERA in domains
    assert Platform.SENSOR in domains
    assert Platform.BINARY_SENSOR in domains
    assert Platform.SWITCH in domains

    assert any(entity.unique_id.startswith("visionmesh_cam_abc123") for entity in entities), (
        "Entity unique ids must be derived from the VisionMesh camera id, which is stable for the "
        "camera's whole life — anything derived from an address breaks on a DHCP lease change."
    )


async def test_ptz_buttons_only_exist_for_a_camera_that_can_move(
    hass: HomeAssistant, mock_client: AsyncMock, config_entry
) -> None:
    """A dead PTZ button on a fixed camera is worse than no button."""
    mock_client.async_get_cameras.return_value = [make_camera(ptz=False)]
    await _setup(hass, config_entry)

    registry = er.async_get(hass)
    entities = er.async_entries_for_config_entry(registry, config_entry.entry_id)
    assert not [e for e in entities if e.domain == Platform.BUTTON], (
        "A camera without PTZ must not get PTZ buttons."
    )


async def test_ptz_buttons_exist_for_a_camera_that_can(
    hass: HomeAssistant, mock_client: AsyncMock, config_entry
) -> None:
    mock_client.async_get_cameras.return_value = [make_camera(ptz=True)]
    await _setup(hass, config_entry)

    registry = er.async_get(hass)
    entities = er.async_entries_for_config_entry(registry, config_entry.entry_id)
    assert [e for e in entities if e.domain == Platform.BUTTON]


async def test_each_camera_becomes_a_device(
    hass: HomeAssistant, mock_client: AsyncMock, config_entry
) -> None:
    """Grouping entities under a device is what makes twenty cameras navigable."""
    mock_client.async_get_cameras.return_value = [
        make_camera("cam_one", "Front Door"),
        make_camera("cam_two", "Garage"),
    ]
    await _setup(hass, config_entry)

    devices = dr.async_entries_for_config_entry(dr.async_get(hass), config_entry.entry_id)
    names = {device.name for device in devices}
    assert "Front Door" in names
    assert "Garage" in names


async def test_entities_go_unavailable_when_the_server_stops_answering(
    hass: HomeAssistant, mock_client: AsyncMock, config_entry
) -> None:
    """Stale readings are worse than an honest 'unavailable'."""
    await _setup(hass, config_entry)

    registry = er.async_get(hass)
    sensors = [
        e for e in er.async_entries_for_config_entry(registry, config_entry.entry_id)
        if e.domain == Platform.SENSOR
    ]
    assert sensors, "expected at least one sensor"

    mock_client.async_get_cameras.side_effect = VisionMeshError("server went away")

    coordinator = config_entry.runtime_data if hasattr(config_entry, "runtime_data") else None
    if coordinator is None:
        coordinator = hass.data[DOMAIN][config_entry.entry_id]
    if hasattr(coordinator, "coordinator"):
        coordinator = coordinator.coordinator

    await coordinator.async_refresh()
    await hass.async_block_till_done()

    states = [hass.states.get(e.entity_id) for e in sensors]
    assert any(s is not None and s.state == STATE_UNAVAILABLE for s in states), (
        "Entities must report unavailable when the server cannot be reached."
    )
