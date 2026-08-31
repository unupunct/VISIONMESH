"""Async client for the VisionMesh REST API.

Kept deliberately thin: it authenticates, fetches camera state, and issues the handful of
commands the entities expose. Everything else Home Assistant needs it already has.

Live video is not fetched through this client. The camera entity streams MJPEG straight from the
server, so a 20-camera wall does not pump every frame through the integration's event loop.
"""

from __future__ import annotations

import asyncio
import logging
from typing import Any

import aiohttp

_LOGGER = logging.getLogger(__name__)

# A snapshot can legitimately take a few seconds when a camera has to be woken first.
DEFAULT_TIMEOUT = aiohttp.ClientTimeout(total=15)
SNAPSHOT_TIMEOUT = aiohttp.ClientTimeout(total=20)


class VisionMeshError(Exception):
    """Something went wrong talking to VisionMesh."""


class VisionMeshAuthError(VisionMeshError):
    """The server rejected the credentials, or the session expired."""


class VisionMeshConnectionError(VisionMeshError):
    """The server could not be reached."""


class VisionMeshClient:
    """Talks to one VisionMesh server."""

    def __init__(
        self,
        session: aiohttp.ClientSession,
        base_url: str,
        username: str,
        password: str,
        verify_ssl: bool = True,
    ) -> None:
        self._session = session
        self._base_url = base_url.rstrip("/")
        self._username = username
        self._password = password
        self._verify_ssl = verify_ssl
        self._token: str | None = None
        # Guards the login so a burst of parallel calls after an expiry does not produce a burst
        # of logins, each invalidating the last.
        self._login_lock = asyncio.Lock()

    @property
    def base_url(self) -> str:
        return self._base_url

    @property
    def token(self) -> str | None:
        return self._token

    async def async_login(self) -> None:
        """Signs in and stores the session token."""
        async with self._login_lock:
            try:
                async with self._session.post(
                    f"{self._base_url}/api/auth/login",
                    json={"username": self._username, "password": self._password},
                    timeout=DEFAULT_TIMEOUT,
                    ssl=self._verify_ssl,
                ) as response:
                    body = await self._read_json(response)

                    if response.status == 401:
                        raise VisionMeshAuthError(body.get("error", "The username or password was not accepted."))
                    if response.status >= 400:
                        raise VisionMeshError(body.get("error", f"VisionMesh returned HTTP {response.status}."))

                    self._token = body.get("token")
                    if not self._token:
                        raise VisionMeshError("VisionMesh did not return a session token.")

            except aiohttp.ClientError as error:
                raise VisionMeshConnectionError(f"Could not reach VisionMesh: {error}") from error
            except asyncio.TimeoutError as error:
                raise VisionMeshConnectionError("VisionMesh did not answer in time.") from error

    async def _request(
        self,
        method: str,
        path: str,
        *,
        json_body: Any | None = None,
        retry_on_auth: bool = True,
    ) -> Any:
        """Performs a request, signing in first or again as needed."""
        if self._token is None:
            await self.async_login()

        headers = {"Authorization": f"Bearer {self._token}"}

        try:
            async with self._session.request(
                method,
                f"{self._base_url}{path}",
                json=json_body,
                headers=headers,
                timeout=DEFAULT_TIMEOUT,
                ssl=self._verify_ssl,
            ) as response:
                if response.status == 401 and retry_on_auth:
                    # The session expired or was revoked. One retry after a fresh login covers
                    # the normal case; a second failure is a real credentials problem.
                    self._token = None
                    return await self._request(method, path, json_body=json_body, retry_on_auth=False)

                body = await self._read_json(response)

                if response.status == 401:
                    raise VisionMeshAuthError(body.get("error", "VisionMesh rejected the stored credentials."))
                if response.status == 403:
                    raise VisionMeshAuthError(
                        body.get("error", "This VisionMesh account does not have permission for that action.")
                    )
                if response.status >= 400:
                    raise VisionMeshError(body.get("error", f"VisionMesh returned HTTP {response.status}."))

                return body

        except aiohttp.ClientError as error:
            raise VisionMeshConnectionError(f"Could not reach VisionMesh: {error}") from error
        except asyncio.TimeoutError as error:
            raise VisionMeshConnectionError("VisionMesh did not answer in time.") from error

    @staticmethod
    async def _read_json(response: aiohttp.ClientResponse) -> Any:
        """Reads a JSON body, tolerating an empty or non-JSON one."""
        text = await response.text()
        if not text:
            return {}
        try:
            return await response.json(content_type=None)
        except (ValueError, aiohttp.ContentTypeError):
            return {"error": text[:200]}

    # ---- reads ----

    async def async_get_system(self) -> dict[str, Any]:
        return await self._request("GET", "/api/system")

    async def async_get_cameras(self) -> list[dict[str, Any]]:
        """Cameras in the shape the integration consumes, including stream and snapshot URLs."""
        return await self._request("GET", "/api/homeassistant/entities")

    async def async_get_storage(self) -> dict[str, Any]:
        return await self._request("GET", "/api/storage")

    async def async_get_recent_events(self, camera_id: str | None = None, limit: int = 20) -> list[dict[str, Any]]:
        query = f"?limit={limit}" + (f"&cameraId={camera_id}" if camera_id else "")
        result = await self._request("GET", f"/api/events{query}")
        return result.get("items", []) if isinstance(result, dict) else []

    async def async_get_snapshot(self, camera_id: str) -> bytes | None:
        """Fetches a still image. Returns None when the camera cannot produce one right now."""
        if self._token is None:
            await self.async_login()

        try:
            async with self._session.get(
                f"{self._base_url}/api/cameras/{camera_id}/snapshot.jpg",
                headers={"Authorization": f"Bearer {self._token}"},
                timeout=SNAPSHOT_TIMEOUT,
                ssl=self._verify_ssl,
            ) as response:
                if response.status == 401:
                    self._token = None
                    await self.async_login()
                    return await self.async_get_snapshot(camera_id)

                if response.status != 200:
                    # 403 is privacy mode and 503 is "no frame yet"; both are normal states, not
                    # faults, so they are logged quietly and the entity simply shows no image.
                    _LOGGER.debug("Snapshot for %s returned HTTP %s", camera_id, response.status)
                    return None

                return await response.read()

        except (aiohttp.ClientError, asyncio.TimeoutError) as error:
            _LOGGER.debug("Snapshot for %s failed: %s", camera_id, error)
            return None

    # ---- commands ----

    async def async_set_privacy(self, camera_id: str, enabled: bool) -> None:
        await self._request("POST", f"/api/cameras/{camera_id}/privacy?enabled={str(enabled).lower()}")

    async def async_set_recording(self, camera_id: str, start: bool) -> None:
        await self._request("POST", f"/api/cameras/{camera_id}/record?start={str(start).lower()}")

    async def async_ptz(self, camera_id: str, pan: float = 0, tilt: float = 0, zoom: float = 0, stop: bool = False) -> None:
        await self._request(
            "POST",
            f"/api/cameras/{camera_id}/ptz",
            json_body={"pan": pan, "tilt": tilt, "zoom": zoom, "stop": stop},
        )

    def stream_url(self, camera_id: str, token: str) -> str:
        """Live MJPEG URL for a camera, authorised by a short-lived camera-scoped token."""
        return f"{self._base_url}/api/cameras/{camera_id}/stream.mjpeg?token={token}"

    async def async_create_stream_token(self, camera_id: str) -> str | None:
        """Issues a token that authorises this camera's stream for a couple of minutes."""
        try:
            result = await self._request("POST", f"/api/cameras/{camera_id}/stream-token")
            return result.get("token")
        except VisionMeshError as error:
            _LOGGER.debug("Could not create a stream token for %s: %s", camera_id, error)
            return None
