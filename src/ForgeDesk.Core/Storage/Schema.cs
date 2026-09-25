namespace ForgeDesk.Core.Storage;

/// <summary>
/// Ordered list of schema migrations. Never edit a shipped migration: append a new one.
/// Each script runs inside a transaction and bumps PRAGMA user_version.
/// </summary>
internal static class Schema
{
    public static readonly IReadOnlyList<string> Migrations =
    [
        // v1 — initial schema (ForgeDesk 1.0)
        """
        CREATE TABLE settings (
            key        TEXT PRIMARY KEY NOT NULL,
            value_json TEXT NOT NULL
        );

        CREATE TABLE projects (
            id               TEXT PRIMARY KEY NOT NULL,
            name             TEXT NOT NULL,
            path             TEXT NOT NULL,
            added_at         TEXT NOT NULL,
            last_opened_at   TEXT NULL,
            is_pinned        INTEGER NOT NULL DEFAULT 0,
            group_name       TEXT NULL,
            color            TEXT NULL,
            sort_order       INTEGER NOT NULL DEFAULT 0,
            github_owner     TEXT NULL,
            github_repo      TEXT NULL,
            profile_json     TEXT NULL,
            profile_at       TEXT NULL,
            snapshot_json    TEXT NULL,
            snapshot_at      TEXT NULL,
            health_json      TEXT NULL,
            health_at        TEXT NULL
        );
        CREATE UNIQUE INDEX ux_projects_path ON projects(path COLLATE NOCASE);

        CREATE TABLE work_items (
            id           TEXT PRIMARY KEY NOT NULL,
            project_id   TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            number       INTEGER NOT NULL,
            title        TEXT NOT NULL,
            description  TEXT NOT NULL DEFAULT '',
            status       INTEGER NOT NULL,
            priority     INTEGER NOT NULL,
            labels       TEXT NOT NULL DEFAULT '',
            created_at   TEXT NOT NULL,
            updated_at   TEXT NOT NULL,
            completed_at TEXT NULL,
            due_at       TEXT NULL,
            sort_order   REAL NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX ux_work_items_number ON work_items(project_id, number);
        CREATE INDEX ix_work_items_project_status ON work_items(project_id, status);

        CREATE TABLE work_item_links (
            id           TEXT PRIMARY KEY NOT NULL,
            work_item_id TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
            kind         INTEGER NOT NULL,
            value        TEXT NOT NULL,
            label        TEXT NULL,
            created_at   TEXT NOT NULL
        );
        CREATE INDEX ix_work_item_links_item ON work_item_links(work_item_id);

        CREATE TABLE work_item_events (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            work_item_id TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
            at           TEXT NOT NULL,
            kind         INTEGER NOT NULL,
            summary      TEXT NOT NULL,
            old_value    TEXT NULL,
            new_value    TEXT NULL
        );
        CREATE INDEX ix_work_item_events_item ON work_item_events(work_item_id, at);

        CREATE TABLE activity (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id  TEXT NULL REFERENCES projects(id) ON DELETE CASCADE,
            at          TEXT NOT NULL,
            kind        INTEGER NOT NULL,
            outcome     INTEGER NOT NULL,
            title       TEXT NOT NULL,
            detail      TEXT NULL,
            ref_kind    TEXT NULL,
            ref_value   TEXT NULL
        );
        CREATE INDEX ix_activity_project_at ON activity(project_id, at DESC);
        CREATE INDEX ix_activity_at ON activity(at DESC);

        CREATE TABLE runs (
            id            TEXT PRIMARY KEY NOT NULL,
            project_id    TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            command_id    TEXT NULL,
            label         TEXT NOT NULL,
            command_line  TEXT NOT NULL,
            working_dir   TEXT NOT NULL,
            category      INTEGER NOT NULL,
            status        INTEGER NOT NULL,
            started_at    TEXT NOT NULL,
            ended_at      TEXT NULL,
            exit_code     INTEGER NULL,
            log_path      TEXT NOT NULL,
            error_summary TEXT NULL,
            line_count    INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_runs_project_started ON runs(project_id, started_at DESC);

        CREATE TABLE custom_commands (
            id           TEXT PRIMARY KEY NOT NULL,
            project_id   TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            name         TEXT NOT NULL,
            command_line TEXT NOT NULL,
            working_dir  TEXT NULL,
            category     INTEGER NOT NULL,
            created_at   TEXT NOT NULL
        );
        CREATE INDEX ix_custom_commands_project ON custom_commands(project_id);
        """,
    ];
}
