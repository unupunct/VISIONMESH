"""Sensors: measured camera statistics and server-wide counts.

Every value here is something VisionMesh actually measured. Where it has not measured something
yet - a frame rate before enough frames have arrived - the sensor reports unknown rather than
zero, because an automation cannot tell those apart otherwise.
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

from homeassistant.components.sensor import (
    SensorDeviceClass,
    SensorEntity,
    SensorEntityDescription,
    SensorStateClass,
)
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import EntityCategory, UnitOfDataRate, UnitOfInformation, UnitOfTime
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .const import DOMAIN
from .coordinator import VisionMeshCoordinator
from .entity import VisionMeshCameraEntity, VisionMeshServerEntity


@dataclass(frozen=True, kw_only=True)
class VisionMeshCameraSensorDescription(SensorEntityDescription):
    """Describes a sensor derived from one camera's state."""

    value: Callable[[dict[str, Any]], Any]


CAMERA_SENSORS: tuple[VisionMeshCameraSensorDescription, ...] = (
    VisionMeshCameraSensorDescription(
        key="fps",
        translation_key="fps",
        name="Frame rate",
        native_unit_of_measurement="fps",
        state_class=SensorStateClass.MEASUREMENT,
        icon="mdi:speedometer",
        value=lambda camera: (camera.get("health") or {}).get("fps"),
    ),
    VisionMeshCameraSensorDescription(
        key="bitrate",
        translation_key="bitrate",
        name="Bitrate",
        native_unit_of_measurement=UnitOfDataRate.KILOBITS_PER_SECOND,
        device_class=SensorDeviceClass.DATA_RATE,
        state_class=SensorStateClass.MEASUREMENT,
        icon="mdi:transmission-tower",
        value=lambda camera: (
            round((camera.get("health") or {}).get("bitrateBps") / 1000, 1)
            if (camera.get("health") or {}).get("bitrateBps") is not None
            else None
        ),
    ),
    VisionMeshCameraSensorDescription(
        key="latency",
        translation_key="latency",
        name="Latency",
        native_unit_of_measurement=UnitOfTime.MILLISECONDS,
        state_class=SensorStateClass.MEASUREMENT,
        icon="mdi:timer-outline",
        entity_category=EntityCategory.DIAGNOSTIC,
        value=lambda camera: (
            round(latency) if (latency := (camera.get("health") or {}).get("latencyMs")) is not None else None
        ),
    ),
    VisionMeshCameraSensorDescription(
        key="state",
        translation_key="state",
        name="State",
        icon="mdi:cctv",
        value=lambda camera: camera.get("state"),
    ),
    VisionMeshCameraSensorDescription(
        key="battery",
        translation_key="battery",
        name="Battery",
        native_unit_of_measurement="%",
        device_class=SensorDeviceClass.BATTERY,
        state_class=SensorStateClass.MEASUREMENT,
        value=lambda camera: (camera.get("health") or {}).get("batteryPercent"),
    ),
)


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback) -> None:
    coordinator: VisionMeshCoordinator = hass.data[DOMAIN][entry.entry_id]
    selected = entry.options.get("cameras")

    entities: list[SensorEntity] = []

    for camera_id, camera in coordinator.cameras.items():
        if selected is not None and camera_id not in selected:
            continue

        for description in CAMERA_SENSORS:
            # A battery sensor on a mains-powered camera would sit permanently unknown, so it is
            # only created for cameras that actually report one.
            if description.key == "battery" and (camera.get("health") or {}).get("batteryPercent") is None:
                continue
            entities.append(VisionMeshCameraSensor(coordinator, camera_id, description))

    entities.extend(
        [
            VisionMeshServerSensor(coordinator, "cameras_online", "Cameras online", "mdi:cctv",
                                   lambda system: system.get("camerasOnline")),
            VisionMeshServerSensor(coordinator, "cameras_total", "Cameras total", "mdi:cctv",
                                   lambda system: system.get("cameraCount")),
            VisionMeshServerSensor(coordinator, "cameras_recording", "Cameras recording", "mdi:record-rec",
                                   lambda system: system.get("camerasRecording")),
            VisionMeshStorageSensor(coordinator),
        ]
    )

    async_add_entities(entities)


class VisionMeshCameraSensor(VisionMeshCameraEntity, SensorEntity):
    """One measured value from one camera."""

    entity_description: VisionMeshCameraSensorDescription

    def __init__(
        self,
        coordinator: VisionMeshCoordinator,
        camera_id: str,
        description: VisionMeshCameraSensorDescription,
    ) -> None:
        super().__init__(coordinator, camera_id, description.key)
        self.entity_description = description

    @property
    def native_value(self) -> Any:
        return self.entity_description.value(self._camera)


class VisionMeshServerSensor(VisionMeshServerEntity, SensorEntity):
    """A count that describes the whole server."""

    _attr_state_class = SensorStateClass.MEASUREMENT

    def __init__(
        self,
        coordinator: VisionMeshCoordinator,
        key: str,
        name: str,
        icon: str,
        value: Callable[[dict[str, Any]], Any],
    ) -> None:
        super().__init__(coordinator, key)
        self._attr_name = name
        self._attr_icon = icon
        self._value = value

    @property
    def native_value(self) -> Any:
        return self._value(self.coordinator.system)


class VisionMeshStorageSensor(VisionMeshServerEntity, SensorEntity):
    """How much disk the recordings are using."""

    _attr_name = "Storage used"
    _attr_icon = "mdi:harddisk"
    _attr_device_class = SensorDeviceClass.DATA_SIZE
    _attr_native_unit_of_measurement = UnitOfInformation.GIGABYTES
    _attr_state_class = SensorStateClass.MEASUREMENT
    _attr_suggested_display_precision = 1

    def __init__(self, coordinator: VisionMeshCoordinator) -> None:
        super().__init__(coordinator, "storage_used")

    @property
    def native_value(self) -> float | None:
        storage = self.coordinator.system.get("storage") or {}
        used = storage.get("usedByRecordingsBytes")
        return round(used / (1024 ** 3), 2) if used is not None else None

    @property
    def extra_state_attributes(self) -> dict[str, Any]:
        storage = self.coordinator.system.get("storage") or {}
        attributes: dict[str, Any] = {}

        if storage.get("freeBytes") is not None:
            attributes["free_gb"] = round(storage["freeBytes"] / (1024 ** 3), 2)
        if storage.get("totalBytes"):
            attributes["total_gb"] = round(storage["totalBytes"] / (1024 ** 3), 2)
        if storage.get("retentionDays") is not None:
            attributes["retention_days"] = storage["retentionDays"]
        if storage.get("path"):
            attributes["path"] = storage["path"]

        return attributes
