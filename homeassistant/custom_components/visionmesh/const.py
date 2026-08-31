"""Constants shared across the VisionMesh integration."""

DOMAIN = "visionmesh"

CONF_VERIFY_SSL = "verify_ssl"

# How often the coordinator refreshes camera state.
#
# Five seconds is a deliberate compromise. Home Assistant only needs state here - is the camera
# online, is it recording, did it see motion - and the live video comes straight from the server
# over HTTP without touching this loop. Polling faster would add load for no visible benefit;
# slower would make motion automations feel sluggish.
UPDATE_INTERVAL_SECONDS = 5

# The server issues session tokens valid for 30 days. Re-authenticating well before that keeps a
# long-running Home Assistant instance from silently losing its session.
TOKEN_REFRESH_DAYS = 20

ATTR_CAMERA_ID = "camera_id"
ATTR_SOURCE_KIND = "source_kind"
ATTR_GROUP = "group"

MANUFACTURER = "VisionMesh"
