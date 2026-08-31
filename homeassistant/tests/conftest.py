"""Fixtures for the VisionMesh integration tests.

These run the integration inside a real Home Assistant core, which is the only way to find out
whether it actually works there. The VisionMesh server itself is replaced by a stub that returns
the payloads the real server returns — the shapes are taken from
`server/VisionMesh.Api/Endpoints/HomeAssistantEndpoints.cs` and `SystemEndpoints.cs`, and the
`test_payload_shapes` module checks they have not drifted apart.
"""

from __future__ import annotations

from collections.abc import Generator
from typing import Any
from unittest.mock import AsyncMock, patch

import pytest
from pytest_homeassistant_custom_component.common import MockConfigEntry

from homeassistant.const import CONF_PASSWORD, CONF_URL, CONF_USERNAME

from .payloads import STORAGE, SYSTEM, SERVER_URL, make_camera

pytest_plugins = "pytest_homeassistant_custom_component"

DOMAIN = "visionmesh"

__all__ = ["DOMAIN", "SERVER_URL", "make_camera"]


@pytest.fixture(autouse=True)
def auto_enable_custom_integrations(enable_custom_integrations):
    """Home Assistant ignores custom_components in tests unless this is requested."""
    yield


@pytest.fixture
def config_entry() -> MockConfigEntry:
    return MockConfigEntry(
        domain=DOMAIN,
        title="Home",
        unique_id=SERVER_URL,
        data={
            CONF_URL: SERVER_URL,
            CONF_USERNAME: "admin",
            CONF_PASSWORD: "secret",
            "verify_ssl": True,
        },
    )


@pytest.fixture
def mock_client() -> Generator[AsyncMock, None, None]:
    """Replaces the HTTP client, leaving every other layer real."""
    client = AsyncMock()
    client.base_url = SERVER_URL
    client.token = "session-token"
    client.async_login.return_value = None
    client.async_get_cameras.return_value = [make_camera()]
    client.async_get_system.return_value = SYSTEM
    client.async_get_storage.return_value = STORAGE
    client.async_get_recent_events.return_value = []
    client.async_get_snapshot.return_value = b"\xff\xd8\xff\xdb-not-a-real-jpeg"
    client.async_create_stream_token.return_value = "stream-token"

    with (
        patch("custom_components.visionmesh.VisionMeshClient", return_value=client),
        patch("custom_components.visionmesh.config_flow.VisionMeshClient", return_value=client),
    ):
        yield client
