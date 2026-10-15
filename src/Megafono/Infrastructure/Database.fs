/// Postgres access (raw Npgsql) and the idempotent schema bootstrap (constitution Principle VII)
module Megafono.Infrastructure.Database

open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Npgsql

// Mirrors specs/001-federation-core/data-model.md; every statement is safe to run again.
// Ids that leave Megáfono are never deleted, so no ON DELETE CASCADE anywhere.
let private schema =
    """
    -- Identity
    CREATE TABLE IF NOT EXISTS instance_actor (
        id              smallint PRIMARY KEY CHECK (id = 1),
        public_key_pem  text NOT NULL,
        enc_private_key bytea NOT NULL,
        created_at      timestamptz NOT NULL DEFAULT now()
    );

    CREATE TABLE IF NOT EXISTS local_actors (
        app_user_id        text PRIMARY KEY CHECK (app_user_id ~ '^[A-Za-z0-9_-]{1,64}$'),
        handle             text NOT NULL CHECK (handle ~ '^[a-z0-9_]([a-z0-9_-]{0,28}[a-z0-9_])?$'),
        actor_type         text NOT NULL DEFAULT 'Person' CHECK (actor_type IN ('Person', 'Service', 'Group')),
        locked             boolean NOT NULL DEFAULT false,
        name               text NOT NULL,
        summary            text NULL,
        icon_url           text NULL,
        profile_url        text NOT NULL,
        profile_checked_at timestamptz NOT NULL DEFAULT now(),
        public_key_pem     text NOT NULL,
        enc_private_key    bytea NOT NULL,
        event_seq          bigint NOT NULL DEFAULT 0,
        created_at         timestamptz NOT NULL DEFAULT now(),
        deleted_at         timestamptz NULL
    );

    -- Databases created before handles allowed inner dashes keep the old check: replace it once
    DO $$
    BEGIN
        IF EXISTS (SELECT 1 FROM pg_constraint
                   WHERE conrelid = 'local_actors'::regclass AND conname = 'local_actors_handle_check'
                     AND pg_get_constraintdef(oid) LIKE '%{1,30}%') THEN
            ALTER TABLE local_actors DROP CONSTRAINT local_actors_handle_check;
            ALTER TABLE local_actors ADD CONSTRAINT local_actors_handle_check
                CHECK (handle ~ '^[a-z0-9_]([a-z0-9_-]{0,28}[a-z0-9_])?$');
        END IF;
    END $$;

    CREATE UNIQUE INDEX IF NOT EXISTS ux_local_actors_handle
        ON local_actors (handle) WHERE deleted_at IS NULL;

    -- Remote side
    CREATE TABLE IF NOT EXISTS remote_actors (
        id              text PRIMARY KEY,
        host            text NOT NULL,
        handle          text NULL,
        actor_type      text NOT NULL,
        inbox           text NOT NULL,
        shared_inbox    text NULL,
        followers_url   text NULL,
        name            text NULL,
        icon_url        text NULL,
        profile_url     text NULL,
        key_id          text NOT NULL,
        public_key_pem  text NOT NULL,
        fetched_at      timestamptz NOT NULL DEFAULT now(),
        CONSTRAINT ux_remote_actors_key_id UNIQUE (key_id)
    );

    CREATE INDEX IF NOT EXISTS ix_remote_actors_host ON remote_actors (host);

    CREATE TABLE IF NOT EXISTS remote_hosts (
        host              text PRIMARY KEY,
        first_failure_at  timestamptz NULL,
        last_success_at   timestamptz NULL,
        unreachable_since timestamptz NULL
    );

    -- When an unreachable host was last tried: one delivery an hour is let through to find it again
    ALTER TABLE remote_hosts ADD COLUMN IF NOT EXISTS last_probe_at timestamptz NULL;

    -- Relationships
    CREATE TABLE IF NOT EXISTS follows (
        id                 uuid PRIMARY KEY,
        direction          text NOT NULL CHECK (direction IN ('inbound', 'outbound')),
        local_actor        text NOT NULL REFERENCES local_actors (app_user_id),
        remote_actor       text NOT NULL REFERENCES remote_actors (id),
        follow_activity_id text NOT NULL,
        status             text NOT NULL CHECK (status IN ('pending', 'accepted', 'rejected', 'removed')),
        created_at         timestamptz NOT NULL DEFAULT now(),
        updated_at         timestamptz NOT NULL DEFAULT now(),
        CONSTRAINT ux_follows_pair UNIQUE (direction, local_actor, remote_actor)
    );

    -- Content
    CREATE TABLE IF NOT EXISTS objects (
        id              uuid PRIMARY KEY,
        local_actor     text NOT NULL REFERENCES local_actors (app_user_id),
        audience        text NOT NULL CHECK (audience IN ('public', 'unlisted', 'followers')),
        object_type     text NOT NULL CHECK (object_type IN ('Note', 'Article')),
        document        jsonb NOT NULL,
        idempotency_key text NULL,
        request_hash    bytea NULL,
        state           text NOT NULL DEFAULT 'live' CHECK (state IN ('live', 'deleted')),
        created_at      timestamptz NOT NULL DEFAULT now(),
        updated_at      timestamptz NOT NULL DEFAULT now(),
        deleted_at      timestamptz NULL,
        CONSTRAINT ux_objects_idempotency UNIQUE (local_actor, idempotency_key)
    );

    -- Replies and threads (specs/002-replies-threads/data-model.md): a reply points at its parent, a root has neither
    ALTER TABLE objects ADD COLUMN IF NOT EXISTS in_reply_to text NULL;
    ALTER TABLE objects ADD COLUMN IF NOT EXISTS thread_root uuid NULL REFERENCES objects (id);
    CREATE INDEX IF NOT EXISTS ix_objects_thread_root ON objects (thread_root) WHERE thread_root IS NOT NULL;

    -- Every reply in one of our threads, ours or remote. References only: the content lives in the app (R-27).
    CREATE TABLE IF NOT EXISTS replies (
        id            text PRIMARY KEY,
        local_object  uuid NULL REFERENCES objects (id),
        remote_actor  text NULL REFERENCES remote_actors (id),
        parent        text NOT NULL,
        parent_object uuid NULL REFERENCES objects (id),
        thread_root   uuid NOT NULL REFERENCES objects (id),
        audience      text NOT NULL CHECK (audience IN ('public', 'unlisted', 'followers', 'direct')),
        state         text NOT NULL DEFAULT 'live' CHECK (state IN ('live', 'deleted')),
        published_at  timestamptz NOT NULL,
        first_seen_at timestamptz NOT NULL DEFAULT now(),
        updated_at    timestamptz NOT NULL DEFAULT now(),
        deleted_at    timestamptz NULL,
        CONSTRAINT ck_replies_one_author CHECK ((local_object IS NULL) <> (remote_actor IS NULL))
    );

    CREATE INDEX IF NOT EXISTS ix_replies_parent ON replies (parent, state);
    CREATE INDEX IF NOT EXISTS ix_replies_thread ON replies (thread_root, state);
    CREATE UNIQUE INDEX IF NOT EXISTS ux_replies_local_object ON replies (local_object) WHERE local_object IS NOT NULL;

    CREATE TABLE IF NOT EXISTS activities (
        id            uuid PRIMARY KEY,
        local_actor   text NOT NULL REFERENCES local_actors (app_user_id),
        object        uuid NULL REFERENCES objects (id),
        activity_type text NOT NULL
            CHECK (activity_type IN ('Create', 'Update', 'Delete', 'Accept', 'Reject', 'Follow', 'Undo')),
        audience      text NOT NULL CHECK (audience IN ('public', 'unlisted', 'followers', 'direct')),
        body          bytea NOT NULL,
        created_at    timestamptz NOT NULL DEFAULT now()
    );

    -- Queues
    CREATE TABLE IF NOT EXISTS deliveries (
        id              uuid PRIMARY KEY,
        activity        uuid NOT NULL REFERENCES activities (id),
        inbox_url       text NOT NULL,
        host            text NOT NULL,
        status          text NOT NULL DEFAULT 'pending'
            CHECK (status IN ('pending', 'sending', 'delivered', 'failed', 'skipped')),
        attempts        int NOT NULL DEFAULT 0,
        next_attempt_at timestamptz NOT NULL DEFAULT now(),
        lease_until     timestamptz NULL,
        lease_token     uuid NULL,
        deadline_at     timestamptz NOT NULL DEFAULT (now() + interval '48 hours'),
        last_error      text NULL,
        delivered_at    timestamptz NULL,
        created_at      timestamptz NOT NULL DEFAULT now(),
        CONSTRAINT ux_deliveries_activity_inbox UNIQUE (activity, inbox_url)
    );

    CREATE INDEX IF NOT EXISTS ix_deliveries_due
        ON deliveries (next_attempt_at) WHERE status IN ('pending', 'sending');

    CREATE TABLE IF NOT EXISTS app_events (
        id              text PRIMARY KEY,
        local_actor     text NOT NULL REFERENCES local_actors (app_user_id),
        seq             bigint NOT NULL,
        event_type      text NOT NULL,
        payload         jsonb NOT NULL,
        status          text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'sending', 'delivered')),
        attempts        int NOT NULL DEFAULT 0,
        next_attempt_at timestamptz NOT NULL DEFAULT now(),
        lease_until     timestamptz NULL,
        lease_token     uuid NULL,
        last_error      text NULL,
        created_at      timestamptz NOT NULL DEFAULT now(),
        delivered_at    timestamptz NULL,
        CONSTRAINT ux_app_events_seq UNIQUE (local_actor, seq)
    );

    CREATE INDEX IF NOT EXISTS ix_app_events_due
        ON app_events (next_attempt_at) WHERE status IN ('pending', 'sending');

    CREATE TABLE IF NOT EXISTS processed_activities (
        activity_id text PRIMARY KEY,
        received_at timestamptz NOT NULL DEFAULT now()
    );

    CREATE INDEX IF NOT EXISTS ix_processed_activities_received ON processed_activities (received_at);
    """

// Arbitrary constant; serializes concurrent starts (two replicas, or tests) so CREATE ... IF NOT EXISTS cannot race
let private schemaLockKey = 7_203_411_001L

/// Creates every table, constraint and index that is missing. Safe to call on every start.
let initializeSchema (dataSource: NpgsqlDataSource) : Task =
    task {
        use! connection = dataSource.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync()

        use lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction)
        lockCommand.Parameters.AddWithValue("key", schemaLockKey) |> ignore
        let! _ = lockCommand.ExecuteNonQueryAsync()

        use ddl = new NpgsqlCommand(schema, connection, transaction)
        let! _ = ddl.ExecuteNonQueryAsync()
        do! transaction.CommitAsync()
    }

/// Registers the data source (disposed with the container) and a hosted service that creates the schema
/// before the app starts serving.
let register (services: IServiceCollection) (connectionString: string) : IServiceCollection =
    services.AddSingleton<NpgsqlDataSource>(fun _ -> NpgsqlDataSource.Create connectionString)
    |> ignore

    services.AddSingleton<IHostedService>(fun sp ->
        let dataSource = sp.GetRequiredService<NpgsqlDataSource>()

        { new IHostedService with
            member _.StartAsync _ = initializeSchema dataSource
            member _.StopAsync _ = Task.CompletedTask })
