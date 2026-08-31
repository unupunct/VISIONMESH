"""Configuration flow: how VisionMesh gets added from the Home Assistant UI."""

from __future__ import annotations

from ipaddress import ip_address
import logging
import re
from typing import Any
from urllib.parse import urlparse

import voluptuous as vol

from homeassistant.config_entries import ConfigEntry, ConfigFlow, ConfigFlowResult, OptionsFlow
from homeassistant.const import CONF_PASSWORD, CONF_URL, CONF_USERNAME
from homeassistant.core import callback
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.service_info.zeroconf import ZeroconfServiceInfo

from .api import VisionMeshAuthError, VisionMeshClient, VisionMeshConnectionError, VisionMeshError
from .const import CONF_VERIFY_SSL, DOMAIN

_LOGGER = logging.getLogger(__name__)


class VisionMeshConfigFlow(ConfigFlow, domain=DOMAIN):
    """Adds a VisionMesh server, by discovery or by hand."""

    VERSION = 1

    def __init__(self) -> None:
        self._discovered_url: str | None = None
        self._discovered_name: str | None = None

    async def async_step_user(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        errors: dict[str, str] = {}

        if user_input is not None:
            url = _normalise_url(user_input[CONF_URL])

            if url is None:
                errors[CONF_URL] = "invalid_url"
            else:
                try:
                    server_name = await _async_validate(
                        self.hass, url, user_input[CONF_USERNAME], user_input[CONF_PASSWORD],
                        user_input.get(CONF_VERIFY_SSL, True),
                    )
                except VisionMeshAuthError:
                    errors["base"] = "invalid_auth"
                except VisionMeshConnectionError:
                    errors["base"] = "cannot_connect"
                except VisionMeshError:
                    errors["base"] = "unknown"
                else:
                    # The URL is the unique id, so adding the same server twice is caught rather
                    # than producing two sets of duplicate entities.
                    await self.async_set_unique_id(url)
                    self._abort_if_unique_id_configured()

                    return self.async_create_entry(
                        title=server_name,
                        data={
                            CONF_URL: url,
                            CONF_USERNAME: user_input[CONF_USERNAME],
                            CONF_PASSWORD: user_input[CONF_PASSWORD],
                            CONF_VERIFY_SSL: user_input.get(CONF_VERIFY_SSL, True),
                        },
                    )

        schema = vol.Schema(
            {
                vol.Required(CONF_URL, default=self._discovered_url or "http://visionmesh.local:8088"): str,
                vol.Required(CONF_USERNAME): str,
                vol.Required(CONF_PASSWORD): str,
                vol.Optional(CONF_VERIFY_SSL, default=True): bool,
            }
        )

        return self.async_show_form(
            step_id="user",
            data_schema=schema,
            errors=errors,
            description_placeholders={"name": self._discovered_name or "VisionMesh"},
        )

    async def async_step_zeroconf(self, discovery_info: ZeroconfServiceInfo) -> ConfigFlowResult:
        """Handles a server found by mDNS, which is how most people will add one."""
        host = discovery_info.host
        port = discovery_info.port or 8088
        properties = discovery_info.properties or {}

        url = f"http://{host}:{port}"
        name = properties.get("name") or discovery_info.name.split(".")[0] or "VisionMesh"

        await self.async_set_unique_id(url)
        self._abort_if_unique_id_configured(updates={CONF_URL: url})

        self._discovered_url = url
        self._discovered_name = name

        # Showing the name in the discovered-devices card is what makes this recognisable rather
        # than an anonymous IP address.
        self.context["title_placeholders"] = {"name": name}

        return await self.async_step_user()

    async def async_step_reauth(self, entry_data: dict[str, Any]) -> ConfigFlowResult:
        """Entered when the stored credentials stop working."""
        self._discovered_url = entry_data.get(CONF_URL)
        return await self.async_step_reauth_confirm()

    async def async_step_reauth_confirm(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        entry = self.hass.config_entries.async_get_entry(self.context["entry_id"])

        if user_input is not None and entry is not None:
            url = entry.data[CONF_URL]
            try:
                await _async_validate(
                    self.hass, url, user_input[CONF_USERNAME], user_input[CONF_PASSWORD],
                    entry.data.get(CONF_VERIFY_SSL, True),
                )
            except VisionMeshAuthError:
                errors["base"] = "invalid_auth"
            except VisionMeshConnectionError:
                errors["base"] = "cannot_connect"
            except VisionMeshError:
                errors["base"] = "unknown"
            else:
                self.hass.config_entries.async_update_entry(
                    entry,
                    data={**entry.data, CONF_USERNAME: user_input[CONF_USERNAME], CONF_PASSWORD: user_input[CONF_PASSWORD]},
                )
                await self.hass.config_entries.async_reload(entry.entry_id)
                return self.async_abort(reason="reauth_successful")

        return self.async_show_form(
            step_id="reauth_confirm",
            data_schema=vol.Schema({vol.Required(CONF_USERNAME): str, vol.Required(CONF_PASSWORD): str}),
            errors=errors,
        )

    @staticmethod
    @callback
    def async_get_options_flow(config_entry: ConfigEntry) -> OptionsFlow:
        return VisionMeshOptionsFlow(config_entry)


class VisionMeshOptionsFlow(OptionsFlow):
    """Lets the user choose which cameras become entities."""

    def __init__(self, config_entry: ConfigEntry) -> None:
        self._entry = config_entry

    async def async_step_init(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        if user_input is not None:
            return self.async_create_entry(title="", data=user_input)

        # Offering the live camera list, rather than a free-text field, means the user picks from
        # what actually exists.
        session = async_get_clientsession(self.hass)
        client = VisionMeshClient(
            session,
            self._entry.data[CONF_URL],
            self._entry.data[CONF_USERNAME],
            self._entry.data[CONF_PASSWORD],
            self._entry.data.get(CONF_VERIFY_SSL, True),
        )

        try:
            cameras = await client.async_get_cameras()
        except VisionMeshError:
            cameras = []

        options = {camera["id"]: camera.get("name", camera["id"]) for camera in cameras}
        selected = self._entry.options.get("cameras") or list(options)

        schema = vol.Schema(
            {
                vol.Optional("cameras", default=selected): vol.All(
                    vol.Coerce(list), [vol.In(list(options))]
                ),
            }
        )

        return self.async_show_form(
            step_id="init",
            data_schema=schema,
            description_placeholders={"count": str(len(options))},
        )


async def _async_validate(hass, url: str, username: str, password: str, verify_ssl: bool) -> str:
    """Signs in and returns the server's own name, proving the whole path works."""
    session = async_get_clientsession(hass)
    client = VisionMeshClient(session, url, username, password, verify_ssl)

    await client.async_login()
    system = await client.async_get_system()
    return system.get("serverName") or "VisionMesh"


def _normalise_url(value: str) -> str | None:
    """Accepts what a person would actually type and turns it into a usable base URL.

    Returns None for anything unusable, so the flow can say "that is not an address" on the form
    rather than accepting it and failing later with a connection error that blames the network.
    """
    value = (value or "").strip()
    if not value:
        return None

    # People type "192.168.1.10:8088" far more often than they type a full URL.
    if "://" not in value:
        value = f"http://{value}"

    parsed = urlparse(value)
    if parsed.scheme not in ("http", "https"):
        return None

    hostname = parsed.hostname
    if not hostname or not _is_plausible_host(hostname):
        return None

    try:
        port = parsed.port
    except ValueError:
        # urlparse only validates the port when it is read, and a number out of range raises.
        return None

    # Rebuilt rather than trimmed, so a trailing slash, a stray path, a query or credentials in
    # the address cannot survive into the base URL every later request is built from.
    host = f"[{hostname}]" if ":" in hostname else hostname
    return f"{parsed.scheme}://{host}" + (f":{port}" if port else "")


def _is_plausible_host(hostname: str) -> bool:
    """Whether this could be a host at all, as opposed to a sentence someone typed."""
    if ":" in hostname:
        # urlparse strips the brackets from an IPv6 literal.
        try:
            ip_address(hostname)
        except ValueError:
            return False
        return True

    return _HOSTNAME.match(hostname) is not None


# Labels of letters, digits and hyphens, separated by dots. Deliberately permissive about what a
# name means and strict about what a name *is*: the old check accepted "not a url" because
# urlparse is happy to call anything without a slash a hostname.
_HOSTNAME = re.compile(
    r"^(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))*\.?$",
    re.IGNORECASE,
)
