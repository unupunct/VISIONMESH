"""Binary sensors: online state and motion.

These are the two things automations actually hang off, which is why they are binary sensors with
proper device classes rather than attributes buried on the camera entity.
"""

from __future__ import annotations

from homeassistant.components.binary_sensor import BinarySensorDeviceClass, BinarySensorEntity
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .const import DOMAIN
from .coordinator import VisionMeshCoordinator
from .entity import VisionMeshCameraEntity


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback) -> None:
    coordinator: VisionMeshCoordinator = hass.data[DOMAIN][entry.entry_id]
    selected = entry.options.get("cameras")

    entities: list[BinarySensorEntity] = []

    for camera_id, camera in coordinator.cameras.items():
        if selected is not None and camera_id not in selected:
            continue

        entities.append(VisionMeshOnlineSensor(coordinator, camera_id))
        entities.append(VisionMeshRecordingSensor(coordinator, camera_id))

        # A motion sensor is only created for cameras set to record on motion. On any other
        # camera it would sit permanently off and look broken.
        if camera.get("supports", {}).get("motion"):
            entities.append(VisionMeshMotionSensor(coordinator, camera_id))

    async_add_entities(entities)


class VisionMeshOnlineSensor(VisionMeshCameraEntity, BinarySensorEntity):
    """Whether the camera is currently sending video."""

    _attr_name = "Online"
    _attr_device_class = BinarySensorDeviceClass.CONNECTIVITY

    def __init__(self, coordinator: VisionMeshCoordinator, camera_id: str) -> None:
        super().__init__(coordinator, camera_id, "online")

    @property
    def is_on(self) -> bool:
        return self._camera.get("state") == "Online"


class VisionMeshRecordingSensor(VisionMeshCameraEntity, BinarySensorEntity):
    """Whether the camera is recording right now."""

    _attr_name = "Recording"
    _attr_icon = "mdi:record-rec"

    def __init__(self, coordinator: VisionMeshCoordinator, camera_id: str) -> None:
        super().__init__(coordinator, camera_id, "recording")

    @property
    def is_on(self) -> bool:
        return bool((self._camera.get("health") or {}).get("recording"))


class VisionMeshMotionSensor(VisionMeshCameraEntity, BinarySensorEntity):
    """Whether VisionMesh is currently seeing motion.

    This reflects movement, not people. VisionMesh does not run a recognition model, so calling
    this a person sensor would be a claim it cannot support.
    """

    _attr_name = "Motion"
    _attr_device_class = BinarySensorDeviceClass.MOTION

    def __init__(self, coordinator: VisionMeshCoordinator, camera_id: str) -> None:
        super().__init__(coordinator, camera_id, "motion")

    @property
    def is_on(self) -> bool:
        return self.coordinator.motion_active.get(self._camera_id, False)
