-- video_db schema, owned by fiapx-video-api.
-- Generated from the EF Core migrations (do not edit by hand):
--   dotnet ef migrations script -p src/FiapX.VideoApi.Infrastructure -s src/FiapX.VideoApi.Infrastructure -o db/schema.sql
-- Equivalent to fiapx/docs/database/02-video_db.sql. Run it against the video_db database.

CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    migration_id character varying(150) NOT NULL,
    product_version character varying(32) NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

START TRANSACTION;
CREATE TABLE inbox_messages (
    message_id uuid NOT NULL,
    consumer text NOT NULL,
    processed_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT pk_inbox_messages PRIMARY KEY (message_id, consumer)
);

CREATE TABLE outbox_messages (
    id uuid NOT NULL,
    type text NOT NULL,
    payload jsonb NOT NULL,
    occurred_at timestamp with time zone NOT NULL DEFAULT (now()),
    published_at timestamp with time zone,
    attempts integer NOT NULL DEFAULT 0,
    CONSTRAINT pk_outbox_messages PRIMARY KEY (id)
);

CREATE TABLE videos (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    original_file_name text NOT NULL,
    content_type text NOT NULL,
    size_bytes bigint NOT NULL,
    storage_key text NOT NULL,
    status text NOT NULL DEFAULT ('PENDING_UPLOAD'),
    frame_count integer,
    result_key text,
    result_size_bytes bigint,
    error_code text,
    error_message text,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    uploaded_at timestamp with time zone,
    processing_started_at timestamp with time zone,
    finished_at timestamp with time zone,
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    version integer NOT NULL DEFAULT 0,
    CONSTRAINT pk_videos PRIMARY KEY (id),
    CONSTRAINT videos_completed_has_result CHECK (status <> 'COMPLETED' OR result_key IS NOT NULL),
    CONSTRAINT videos_failed_has_error CHECK (status <> 'FAILED' OR error_code IS NOT NULL),
    CONSTRAINT videos_size_bytes_check CHECK (size_bytes > 0),
    CONSTRAINT videos_status_check CHECK (status IN ('PENDING_UPLOAD','UPLOADED','PROCESSING','COMPLETED','FAILED','EXPIRED'))
);

CREATE INDEX ix_outbox_pending ON outbox_messages (occurred_at) WHERE published_at IS NULL;

CREATE INDEX ix_videos_open_status ON videos (status, updated_at) WHERE status IN ('PENDING_UPLOAD','UPLOADED','PROCESSING');

CREATE INDEX ix_videos_user_created ON videos (user_id, created_at DESC);

INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
VALUES ('20261123195325_InitialCreate', '10.0.15');

COMMIT;


