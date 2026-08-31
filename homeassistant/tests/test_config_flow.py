"""The flow a user actually walks through when adding a VisionMesh server."""

from __future__ import annotations

import ipaddress
from unittest.mock import AsyncMock

from custom_components.visionmesh.api import (
    VisionMeshAuthError,
    VisionMeshConnectionError,
    VisionMeshError,
)
from custom_components.visionmesh.config_flow import _normalise_url
import pytest
from pytest_homeassistant_custom_component.common import MockConfigEntry

from homeassistant import config_entries
from homeassistant.const import CONF_PASSWORD, CONF_URL, CONF_USERNAME
from homeassistant.core import HomeAssistant
from homeassistant.data_entry_flow import FlowResultType
from homeassistant.helpers.service_info.zeroconf import ZeroconfServiceInfo

from .conftest import DOMAIN, SERVER_URL


def _discovery(properties: dict[str, str]) -> ZeroconfServiceInfo:
    """A server announcing itself over mDNS, as Home Assistant hands it to the flow."""
    address = ipaddress.ip_address("192.168.1.50")
    return ZeroconfServiceInfo(
        ip_address=address,
        ip_addresses=[address],
        hostname="visionmesh.local.",
        name="VisionMesh._visionmesh._tcp.local.",
        port=8088,
        type="_visionmesh._tcp.local.",
        properties=properties,
    )


async def test_user_flow_creates_an_entry(hass: HomeAssistant, mock_client: AsyncMock) -> None:
    result = await hass.config_entries.flow.async_init(
        DOMAIN, context={"source": config_entries.SOURCE_USER}
    )
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "user"

    result = await hass.config_entries.flow.async_configure(
        result["flow_id"],
        {CONF_URL: SERVER_URL, CONF_USERNAME: "admin", CONF_PASSWORD: "secret"},
    )
    await hass.async_block_till_done()

    assert result["type"] is FlowResultType.CREATE_ENTRY
    # The title comes from the server's own name, which is what makes the entry recognisable.
    assert result["title"] == "Home"
    assert result["data"][CONF_URL] == SERVER_URL
    assert result["data"][CONF_USERNAME] == "admin"

    mock_client.async_login.assert_awaited()


@pytest.mark.parametrize(
    ("error", "expected"),
    [
        (VisionMeshAuthError("no"), "invalid_auth"),
        (VisionMeshConnectionError("no"), "cannot_connect"),
        (VisionMeshError("no"), "unknown"),
    ],
)
async def test_failures_are_reported_on_the_form(
    hass: HomeAssistant, mock_client: AsyncMock, error: Exception, expected: str
) -> None:
    """A failure has to come back as a message on the form, not a traceback."""
    mock_client.async_login.side_effect = error

    result = await hass.config_entries.flow.async_init(
        DOMAIN, context={"source": config_entries.SOURCE_USER}
    )
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"],
        {CONF_URL: SERVER_URL, CONF_USERNAME: "admin", CONF_PASSWORD: "wrong"},
    )

    assert result["type"] is FlowResultType.FORM
    assert result["errors"] == {"base": expected}

    # And the form must still be usable once the problem is fixed.
    mock_client.async_login.side_effect = None
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"],
        {CONF_URL: SERVER_URL, CONF_USERNAME: "admin", CONF_PASSWORD: "secret"},
    )
    await hass.async_block_till_done()
    assert result["type"] is FlowResultType.CREATE_ENTRY


async def test_an_unusable_address_is_rejected_before_any_request(
    hass: HomeAssistant, mock_client: AsyncMock
) -> None:
    result = await hass.config_entries.flow.async_init(
        DOMAIN, context={"source": config_entries.SOURCE_USER}
    )
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"],
        {CONF_URL: "not a url", CONF_USERNAME: "admin", CONF_PASSWORD: "secret"},
    )

    assert result["type"] is FlowResultType.FORM
    assert result["errors"] == {CONF_URL: "invalid_url"}
    mock_client.async_login.assert_not_awaited()


async def test_the_same_server_cannot_be_added_twice(
    hass: HomeAssistant, mock_client: AsyncMock, config_entry
) -> None:
    """Otherwise every camera would appear twice, with no obvious way to tell them apart."""
    config_entry.add_to_hass(hass)

    result = await hass.config_entries.flow.async_init(
        DOMAIN, context={"source": config_entries.SOURCE_USER}
    )
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"],
        {CONF_URL: SERVER_URL, CONF_USERNAME: "admin", CONF_PASSWORD: "secret"},
    )

    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "already_configured"


async def test_zeroconf_discovery_offers_the_server_by_name(
    hass: HomeAssistant, mock_client: AsyncMock
) -> None:
    """mDNS is how most people will add a server, so it has to arrive named, not as an IP."""
    result = await hass.config_entries.flow.async_init(
        DOMAIN,
        context={"source": config_entries.SOURCE_ZEROCONF},
        data=_discovery({"name": "Garage Server"}),
    )

    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "user"
    # The name is what shows on the discovered-devices card.
    assert result["description_placeholders"]["name"] == "Garage Server"

    flow = next(f for f in hass.config_entries.flow.async_progress() if f["flow_id"] == result["flow_id"])
    assert flow["context"]["title_placeholders"] == {"name": "Garage Server"}


async def test_zeroconf_ignores_a_server_already_configured(
    hass: HomeAssistant, mock_client: AsyncMock
) -> None:
    """A server that is already set up must stop offering itself on the discovery card."""
    # The unique id is the URL the flow builds from the discovery, so the existing entry has to
    # carry that same URL for this to be the "already configured" case at all.
    discovered_url = "http://192.168.1.50:8088"

    MockConfigEntry(
        domain=DOMAIN,
        title="Home",
        unique_id=discovered_url,
        data={
            CONF_URL: discovered_url,
            CONF_USERNAME: "admin",
            CONF_PASSWORD: "secret",
            "verify_ssl": True,
        },
    ).add_to_hass(hass)

    result = await hass.config_entries.flow.async_init(
        DOMAIN,
        context={"source": config_entries.SOURCE_ZEROCONF},
        data=_discovery({}),
    )

    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "already_configured"


@pytest.mark.parametrize(
    ("typed", "expected"),
    [
        ("192.168.1.10:8088", "http://192.168.1.10:8088"),
        ("http://visionmesh.local:8088/", "http://visionmesh.local:8088"),
        ("https://cams.example.com", "https://cams.example.com"),
        ("  visionmesh.local:8088  ", "http://visionmesh.local:8088"),
        ("", None),
        ("ftp://nope", None),
        ("http://", None),
    ],
)
def test_url_normalisation_accepts_what_people_type(typed: str, expected: str | None) -> None:
    """People type an address and a port far more often than a full URL."""
    assert _normalise_url(typed) == expected


async def test_reauth_replaces_the_stored_credentials(
    hass: HomeAssistant, mock_client: AsyncMock, config_entry
) -> None:
    """When a password changes, the user must be able to fix it without losing every entity."""
    config_entry.add_to_hass(hass)

    result = await config_entry.start_reauth_flow(hass)
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "reauth_confirm"

    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {CONF_USERNAME: "admin", CONF_PASSWORD: "a-new-password"}
    )
    await hass.async_block_till_done()

    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "reauth_successful"
    assert config_entry.data[CONF_PASSWORD] == "a-new-password"
