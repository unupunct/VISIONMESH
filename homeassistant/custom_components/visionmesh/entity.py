"""Shared base class for every VisionMesh entity."""

from __future__ import annotations

from typing import Any

from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from .const import DOMAIN, MANUFACTURER
from .coordinator import VisionMeshCoordinator


class VisionMeshCameraEntity(CoordinatorEntity[VisionMeshCoordinator]):
    """Base for entities that belong to one VisionMesh camera."""

    _attr_has_entity_name = True

    def __init__(self, coordinator: VisionMeshCoordinator, camera_id: str, key: str) -> None:
        super().__init__(coordinator)
        self._camera_id = camera_id

        # The unique id is built from the VisionMesh camera id, which never changes for the life
        # of the camera. Anything derived from an IP address or a name would orphan the entity the
        # first time a DHCP lease or a label changed.
        self._attr_unique_id = f"visionmesh_{camera_id}_{key}"

    @property
    def _camera(self) -> dict[str, Any]:
        return self.coordinator.camera(self._camera_id)

    @property
    def available(self) -> bool:
        # A camera being offline is a state, not an availability problem: the entity should say
        # "off" rather than "unavailable", which is what lets an automation react to it going down.
        return self.coordinator.last_update_success and bool(self._camera)

    @property
    def device_info(self) -> DeviceInfo:
        camera = self._camera
        return DeviceInfo(
            identifiers={(DOMAIN, self._camera_id)},
            name=camera.get("name", self._camera_id),
            manufacturer=MANUFACTURER,
            model=_describe_source(camera.get("sourceKind")),
            via_device=(DOMAIN, self.coordinator.entry.entry_id),
            configuration_url=f"{self.coordinator.client.base_url}/#/camera/{self._camera_id}",
            suggested_area=camera.get("groupName") or None,
        )


class VisionMeshServerEntity(CoordinatorEntity[VisionMeshCoordinator]):
    """Base for entities that describe the server rather than one camera."""

    _attr_has_entity_name = True

    def __init__(self, coordinator: VisionMeshCoordinator, key: str) -> None:
        super().__init__(coordinator)
        self._attr_unique_id = f"visionmesh_server_{coordinator.entry.entry_id}_{key}"

    @property
    def device_info(self) -> DeviceInfo:
        return DeviceInfo(
            identifiers={(DOMAIN, self.coordinator.entry.entry_id)},
            name=self.coordinator.server_name,
            manufacturer=MANUFACTURER,
            model="VisionMesh Server",
            sw_version=self.coordinator.server_version or None,
            configuration_url=self.coordinator.client.base_url,
        )


def _describe_source(source_kind: str | None) -> str:
    return {
        "AgentCamera": "USB camera",
        "AndroidPhone": "Android phone",
        "IosPhone": "iPhone or iPad",
        "Rtsp": "RTSP camera",
        "Onvif": "ONVIF camera",
    }.get(source_kind or "", "Camera")
