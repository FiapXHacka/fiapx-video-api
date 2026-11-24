CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    migration_id character varying(150) NOT NULL,
    product_version character varying(32) NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261123195325_InitialCreate') THEN
    CREATE TABLE inbox_messages (
        message_id uuid NOT NULL,
        consumer text NOT NULL,
        processed_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_inbox_messages PRIMARY KEY (message_id, consumer)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261123195325_InitialCreate') THEN
    CREATE TABLE outbox_messages (
        id uuid NOT NULL,
        type text NOT NULL,
        payload jsonb NOT NULL,
        occurred_at timestamp with time zone NOT NULL DEFAULT (now()),
        published_at timestamp with time zone,
        attempts integer NOT NULL DEFAULT 0,
        CONSTRAINT pk_outbox_messages PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261123195325_InitialCreate') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261123195325_InitialCreate') THEN
    CREATE INDEX ix_outbox_pending ON outbox_messages (occurred_at) WHERE published_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261123195325_InitialCreate') THEN
    CREATE INDEX ix_videos_open_status ON videos (status, updated_at) WHERE status IN ('PENDING_UPLOAD','UPLOADED','PROCESSING');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261123195325_InitialCreate') THEN
    CREATE INDEX ix_videos_user_created ON videos (user_id, created_at DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261123195325_InitialCreate') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261123195325_InitialCreate', '10.0.15');
    END IF;
END $EF$;
COMMIT;

