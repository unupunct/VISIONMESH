namespace VisionMesh.Database.Migrations;

/// <summary>
/// Ordered, append-only list of schema migrations. Index 0 is version 1.
/// Never edit an existing entry once it has shipped - add a new one instead, otherwise
/// upgraded installations and fresh installations end up with different schemas.
/// </summary>
public static class Schema
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        // ---- v1: initial schema -------------------------------------------------
        """
        CREATE TABLE devices (
            id             TEXT PRIMARY KEY,
            name           TEXT NOT NULL,
            kind           INTEGER NOT NULL,
            platform       TEXT NOT NULL DEFAULT '',
            agent_version  TEXT NOT NULL DEFAULT '',
            last_address   TEXT NULL,
            created_utc    TEXT NOT NULL,
            last_seen_utc  TEXT NULL,
            state          INTEGER NOT NULL DEFAULT 0,
            token_hash     TEXT NOT NULL,
            battery_json   TEXT NULL
        );
        CREATE INDEX ix_devices_token ON devices(token_hash);

        CREATE TABLE cameras (
            id              TEXT PRIMARY KEY,
            name            TEXT NOT NULL,
            source_kind     INTEGER NOT NULL,
            device_id       TEXT NULL REFERENCES devices(id) ON DELETE CASCADE,
            source_id       TEXT NULL,
            group_name      TEXT NULL,
            enabled         INTEGER NOT NULL DEFAULT 1,
            state           INTEGER NOT NULL DEFAULT 0,
            recording_mode  INTEGER NOT NULL DEFAULT 0,
            retention_days  INTEGER NOT NULL DEFAULT 7,
            privacy_mode    INTEGER NOT NULL DEFAULT 0,
            audio_enabled   INTEGER NOT NULL DEFAULT 0,
            ptz_supported   INTEGER NOT NULL DEFAULT 0,
            desired_width   INTEGER NOT NULL DEFAULT 1280,
            desired_height  INTEGER NOT NULL DEFAULT 720,
            desired_fps     INTEGER NOT NULL DEFAULT 15,
            desired_quality INTEGER NOT NULL DEFAULT 75,
            created_utc     TEXT NOT NULL,
            config_json     TEXT NULL,
            floorplan_x     REAL NULL,
            floorplan_y     REAL NULL
        );
        CREATE INDEX ix_cameras_device ON cameras(device_id);
        CREATE UNIQUE INDEX ux_cameras_device_source ON cameras(device_id, source_id) WHERE device_id IS NOT NULL AND source_id IS NOT NULL;

        CREATE TABLE users (
            id             TEXT PRIMARY KEY,
            username       TEXT NOT NULL,
            password_hash  TEXT NOT NULL,
            role           INTEGER NOT NULL,
            created_utc    TEXT NOT NULL,
            last_login_utc TEXT NULL,
            disabled       INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX ux_users_username ON users(username COLLATE NOCASE);

        CREATE TABLE sessions (
            token_hash  TEXT PRIMARY KEY,
            user_id     TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            created_utc TEXT NOT NULL,
            expires_utc TEXT NOT NULL,
            address     TEXT NULL,
            user_agent  TEXT NULL
        );
        CREATE INDEX ix_sessions_user ON sessions(user_id);
        CREATE INDEX ix_sessions_expiry ON sessions(expires_utc);

        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE events (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            camera_id     TEXT NULL,
            device_id     TEXT NULL,
            type          INTEGER NOT NULL,
            severity      INTEGER NOT NULL DEFAULT 0,
            timestamp_utc TEXT NOT NULL,
            detail        TEXT NULL,
            recording_id  INTEGER NULL
        );
        CREATE INDEX ix_events_time ON events(timestamp_utc DESC);
        CREATE INDEX ix_events_camera_time ON events(camera_id, timestamp_utc DESC);

        CREATE TABLE recordings (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            camera_id  TEXT NOT NULL,
            file_path  TEXT NOT NULL,
            start_utc  TEXT NOT NULL,
            end_utc    TEXT NULL,
            size_bytes INTEGER NOT NULL DEFAULT 0,
            trigger    INTEGER NOT NULL DEFAULT 0,
            closed     INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_recordings_camera_start ON recordings(camera_id, start_utc DESC);
        CREATE INDEX ix_recordings_open ON recordings(closed) WHERE closed = 0;

        CREATE TABLE audit (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            user_id       TEXT NULL,
            username      TEXT NULL,
            action        TEXT NOT NULL,
            target        TEXT NULL,
            address       TEXT NULL,
            detail        TEXT NULL
        );
        CREATE INDEX ix_audit_time ON audit(timestamp_utc DESC);

        CREATE TABLE pairing_tokens (
            code                TEXT PRIMARY KEY,
            created_utc         TEXT NOT NULL,
            expires_utc         TEXT NOT NULL,
            used                INTEGER NOT NULL DEFAULT 0,
            issued_by_user_id   TEXT NULL,
            consumed_by_device  TEXT NULL
        );
        CREATE INDEX ix_pairing_expiry ON pairing_tokens(expires_utc);
        """,
    };
}
