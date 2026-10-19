/// Postgres access (raw Npgsql) and the idempotent schema bootstrap (constitution Principle VII)
module Megafono.Infrastructure.Database

open System.Threading.Tasks
open Megafono.Domain
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
            CHECK (activity_type IN ('Create', 'Update', 'Delete', 'Accept', 'Reject', 'Follow', 'Undo', 'Block')),
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

    -- A deleted actor's keys are cleared once its Delete deliveries end (specs/003-actor-deletion R-32)
    ALTER TABLE local_actors ALTER COLUMN public_key_pem DROP NOT NULL;
    ALTER TABLE local_actors ALTER COLUMN enc_private_key DROP NOT NULL;

    -- Blocks (specs/004-blocks/data-model.md): a user's (local_actor set) or the instance's (NULL) block of one remote
    -- actor or one domain
    CREATE TABLE IF NOT EXISTS blocks (
        id             uuid PRIMARY KEY,
        local_actor    text NULL REFERENCES local_actors (app_user_id),
        actor          text NULL,
        domain         text NULL,
        block_activity uuid NULL REFERENCES activities (id),
        created_at     timestamptz NOT NULL DEFAULT now(),
        CONSTRAINT ck_blocks_one_target CHECK ((actor IS NULL) <> (domain IS NULL))
    );

    CREATE UNIQUE INDEX IF NOT EXISTS ux_blocks_target
        ON blocks ((COALESCE(local_actor, '')), (COALESCE(actor, '')), (COALESCE(domain, '')));
    CREATE INDEX IF NOT EXISTS ix_blocks_actor ON blocks (actor) WHERE actor IS NOT NULL;
    CREATE INDEX IF NOT EXISTS ix_blocks_domain ON blocks (domain) WHERE domain IS NOT NULL;

    -- Databases created before blocks lack 'Block' in the activity types: replace the check once
    DO $$
    BEGIN
        IF EXISTS (SELECT 1 FROM pg_constraint
                   WHERE conrelid = 'activities'::regclass AND conname = 'activities_activity_type_check'
                     AND pg_get_constraintdef(oid) NOT LIKE '%''Block''%') THEN
            ALTER TABLE activities DROP CONSTRAINT activities_activity_type_check;
            ALTER TABLE activities ADD CONSTRAINT activities_activity_type_check
                CHECK (activity_type IN ('Create', 'Update', 'Delete', 'Accept', 'Reject', 'Follow', 'Undo', 'Block'));
        END IF;
    END $$;

    -- Posts from followed accounts (specs/005-followed-posts/data-model.md): one row per remote post and local user it
    -- was passed to. References only: the content lives in the app.
    CREATE TABLE IF NOT EXISTS feed_posts (
        id            text NOT NULL,
        local_actor   text NOT NULL REFERENCES local_actors (app_user_id),
        remote_actor  text NOT NULL REFERENCES remote_actors (id),
        audience      text NOT NULL CHECK (audience IN ('public', 'unlisted', 'followers', 'direct')),
        state         text NOT NULL DEFAULT 'live' CHECK (state IN ('live', 'deleted')),
        published_at  timestamptz NOT NULL,
        first_seen_at timestamptz NOT NULL DEFAULT now(),
        updated_at    timestamptz NOT NULL DEFAULT now(),
        deleted_at    timestamptz NULL,
        PRIMARY KEY (id, local_actor)
    );
    """

/// The namespaces the app has published with (specs/005-followed-posts R-58). Created and filled once, from the
/// string-valued terms of the stored objects' `@context` that are not Megáfono's own, inside a block that only runs when
/// the table is absent, so later starts do not scan `objects`.
let private extensionNamespaces =
    let ownTerms = ActivityStreams.ownTerms |> Seq.map (fun term -> $"'{term}'") |> String.concat ", "

    $"""
    DO $$
    BEGIN
        IF to_regclass('extension_namespaces') IS NULL THEN
            CREATE TABLE extension_namespaces (
                namespace     text PRIMARY KEY,
                first_used_at timestamptz NOT NULL DEFAULT now()
            );

            INSERT INTO extension_namespaces (namespace)
            SELECT DISTINCT term.value #>> '{{}}'
            FROM objects o,
                 LATERAL jsonb_array_elements(
                     CASE jsonb_typeof(o.document -> '@context')
                         WHEN 'array' THEN o.document -> '@context'
                         ELSE jsonb_build_array(o.document -> '@context')
                     END) AS element,
                 LATERAL jsonb_each(
                     CASE jsonb_typeof(element) WHEN 'object' THEN element ELSE jsonb_build_object() END) AS term
            WHERE jsonb_typeof(term.value) = 'string' AND term.key NOT IN ({ownTerms})
            ON CONFLICT DO NOTHING;
        END IF;
    END $$;
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

        use ddl = new NpgsqlCommand(schema + extensionNamespaces, connection, transaction)
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
