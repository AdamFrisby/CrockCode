using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Storage;

public sealed class SqliteStore : IEventStore, IProjectionStore, IOutbox, IJobStore, ILeaseManager
{
    private readonly string _connectionString;
    private readonly IClock _clock;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SqliteStore(string dbPath, IClock clock)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString + ";Default Timeout=5";
        _clock = clock;
        InitializeDatabase();
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    private void InitializeDatabase()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS events (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id TEXT NOT NULL,
                type TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                idempotency_key TEXT UNIQUE,
                ts TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS outbox (
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL,
                command_type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                idempotency_key TEXT UNIQUE,
                status TEXT NOT NULL,
                attempts INTEGER NOT NULL,
                next_attempt_at TEXT NOT NULL,
                created_at TEXT NOT NULL,
                dispatched_at TEXT
            );

            CREATE TABLE IF NOT EXISTS tasks (
                id TEXT PRIMARY KEY,
                workflow_state TEXT NOT NULL,
                working_dir TEXT NOT NULL,
                prompt TEXT NOT NULL,
                priority INTEGER NOT NULL,
                parent_id TEXT,
                assigned_worker TEXT,
                state_json TEXT NOT NULL,
                version INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS leases (
                working_dir TEXT PRIMARY KEY,
                root_task_id TEXT NOT NULL,
                holder TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                task_id TEXT,
                working_dir TEXT NOT NULL,
                job_type TEXT NOT NULL,
                scheduled_at TEXT NOT NULL,
                max_retries INTEGER NOT NULL,
                attempt INTEGER NOT NULL,
                status TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS workers (
                custom_id TEXT PRIMARY KEY,
                provider_batch_ref TEXT,
                status TEXT NOT NULL,
                model TEXT,
                input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                cost_usd REAL NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS task_transcripts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id TEXT NOT NULL,
                tool_name TEXT NOT NULL,
                arguments_json TEXT NOT NULL,
                result_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
        ";
        cmd.ExecuteNonQuery();
    }

    // ── IEventStore Implementation ──────────────────────────────────────

    public async Task<Result<long>> AppendAsync(TransitionBatch batch, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var txn = conn.BeginTransaction();

            // Verify version
            long currentVersion = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT version FROM tasks WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", batch.TaskId.Value);
                var val = await cmd.ExecuteScalarAsync(ct);
                if (val != null)
                {
                    currentVersion = (long)val;
                }
            }

            if (currentVersion != batch.ExpectedVersion)
            {
                return new Result<long>.Err(new Error.Transient("CONCURRENCY_CONFLICT",
                    $"Expected version {batch.ExpectedVersion} but found {currentVersion} for task {batch.TaskId}"));
            }

            string nowStr = _clock.Now.Value.ToString("O");

            // Insert events
            foreach (var evt in batch.Events)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = txn;
                cmd.CommandText = @"
                    INSERT INTO events (task_id, type, schema_version, payload_json, ts)
                    VALUES (@task_id, @type, 1, @payload_json, @ts)";
                cmd.Parameters.AddWithValue("@task_id", batch.TaskId.Value);
                cmd.Parameters.AddWithValue("@type", evt.GetType().Name);
                cmd.Parameters.AddWithValue("@payload_json", JsonSerializer.Serialize<WorkflowEvent>(evt, JsonOptions));
                cmd.Parameters.AddWithValue("@ts", nowStr);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Upsert projection
            string stateType = batch.NextState.GetType().Name.ToLowerInvariant();
            string stateJson = JsonSerializer.Serialize<WorkflowState>(batch.NextState, JsonOptions);
            string? parentId = batch.Events.OfType<WorkflowEvent.Enqueued>().FirstOrDefault()?.ParentId?.Value;
            string? assignedWorker = null;

            batch.NextState.Match(
                queued => { assignedWorker = null; return Unit.Value; },
                disp => { assignedWorker = disp.WorkerId.Value; return Unit.Value; },
                run => { assignedWorker = run.WorkerId.Value; return Unit.Value; },
                awaiting => { assignedWorker = awaiting.WorkerId.Value; return Unit.Value; },
                comp => { assignedWorker = null; return Unit.Value; },
                failed => { assignedWorker = null; return Unit.Value; },
                suspended => { assignedWorker = suspended.WorkerId.Value; return Unit.Value; },
                retrying => { assignedWorker = null; return Unit.Value; },
                cancelled => { assignedWorker = null; return Unit.Value; }
            );

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = @"
                    INSERT INTO tasks (id, workflow_state, working_dir, prompt, priority, parent_id, assigned_worker, state_json, version, created_at, updated_at)
                    VALUES (@id, @state, @dir, @prompt, @priority, @parent_id, @assigned_worker, @json, @version, @ts, @ts)
                    ON CONFLICT(id) DO UPDATE SET
                        workflow_state = excluded.workflow_state,
                        assigned_worker = excluded.assigned_worker,
                        state_json = excluded.state_json,
                        version = excluded.version,
                        updated_at = excluded.updated_at";
                cmd.Parameters.AddWithValue("@id", batch.TaskId.Value);
                cmd.Parameters.AddWithValue("@state", stateType);
                cmd.Parameters.AddWithValue("@dir", batch.NextState.WorkingDir.Value);
                
                string prompt = "";
                int priority = 0;
                batch.NextState.Match(
                    queued => { prompt = queued.Prompt; priority = queued.Priority.Value; return Unit.Value; },
                    disp => { prompt = disp.Prompt; priority = disp.Priority.Value; return Unit.Value; },
                    run => { prompt = run.Prompt; priority = run.Priority.Value; return Unit.Value; },
                    awaiting => { return Unit.Value; },
                    comp => { return Unit.Value; },
                    failed => { return Unit.Value; },
                    suspended => { prompt = suspended.Prompt; priority = suspended.Priority.Value; return Unit.Value; },
                    retrying => { prompt = retrying.Prompt; priority = retrying.Priority.Value; return Unit.Value; },
                    cancelled => { return Unit.Value; }
                );

                cmd.Parameters.AddWithValue("@prompt", prompt);
                cmd.Parameters.AddWithValue("@priority", priority);
                cmd.Parameters.AddWithValue("@parent_id", (object?)parentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@assigned_worker", (object?)assignedWorker ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@json", stateJson);
                cmd.Parameters.AddWithValue("@version", batch.ExpectedVersion + 1);
                cmd.Parameters.AddWithValue("@ts", nowStr);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Insert commands to outbox
            foreach (var cmd in batch.Commands)
            {
                string cmdType = cmd.GetType().Name;
                string payload = JsonSerializer.Serialize<Command>(cmd, JsonOptions);
                string cmdId = Guid.NewGuid().ToString("N");
                string idemKey = cmdId;

                cmd.Match(
                    submit => { idemKey = submit.IdemKey.Value; return Unit.Value; },
                    emit => { idemKey = $"stream_{emit.Envelope.TaskId.Value}_{emit.Envelope.Seq}"; return Unit.Value; },
                    schedule => { idemKey = $"retry_{schedule.TaskId.Value}_{schedule.Attempt}"; return Unit.Value; },
                    requeue => { idemKey = $"requeue_{requeue.TaskId.Value}"; return Unit.Value; },
                    release => { idemKey = $"release_{release.TaskId.Value}"; return Unit.Value; },
                    cancel => { idemKey = $"cancel_{cancel.WorkerId.Value}"; return Unit.Value; }
                );

                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = txn;
                insertCmd.CommandText = @"
                    INSERT INTO outbox (id, task_id, command_type, payload_json, idempotency_key, status, attempts, next_attempt_at, created_at)
                    VALUES (@id, @task_id, @type, @payload, @idem, 'pending', 0, @ts, @ts)
                    ON CONFLICT(idempotency_key) DO NOTHING";
                insertCmd.Parameters.AddWithValue("@id", cmdId);
                insertCmd.Parameters.AddWithValue("@task_id", batch.TaskId.Value);
                insertCmd.Parameters.AddWithValue("@type", cmdType);
                insertCmd.Parameters.AddWithValue("@payload", payload);
                insertCmd.Parameters.AddWithValue("@idem", idemKey);
                insertCmd.Parameters.AddWithValue("@ts", nowStr);
                await insertCmd.ExecuteNonQueryAsync(ct);
            }

            txn.Commit();
            return Result.Ok(batch.ExpectedVersion + 1);
        }
        catch (Exception ex)
        {
            return new Result<long>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<ImmutableArray<WorkflowEvent>>> LoadEventsAsync(TaskId taskId, long fromVersion = 0, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT type, payload_json FROM events WHERE task_id = @id ORDER BY seq ASC";
            cmd.Parameters.AddWithValue("@id", taskId.Value);

            var builder = ImmutableArray.CreateBuilder<WorkflowEvent>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string payload = reader.GetString(1);
                var evt = JsonSerializer.Deserialize<WorkflowEvent>(payload, JsonOptions);
                if (evt != null)
                {
                    builder.Add(evt);
                }
            }
            return Result.Ok(builder.ToImmutable());
        }
        catch (Exception ex)
        {
            return new Result<ImmutableArray<WorkflowEvent>>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    async Task<Result<long>> IEventStore.GetVersionAsync(TaskId taskId, CancellationToken ct)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE task_id = @id";
            cmd.Parameters.AddWithValue("@id", taskId.Value);
            var val = await cmd.ExecuteScalarAsync(ct);
            return Result.Ok(val != null ? (long)val : 0L);
        }
        catch (Exception ex)
        {
            return new Result<long>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    // ── IProjectionStore Implementation ──────────────────────────────────

    public async Task<Result<bool>> UpsertAsync(TaskId taskId, WorkflowState state, long version, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            string stateType = state.GetType().Name.ToLowerInvariant();
            string stateJson = JsonSerializer.Serialize<WorkflowState>(state, JsonOptions);
            string nowStr = _clock.Now.Value.ToString("O");
            string? assignedWorker = null;

            state.Match(
                queued => { assignedWorker = null; return Unit.Value; },
                disp => { assignedWorker = disp.WorkerId.Value; return Unit.Value; },
                run => { assignedWorker = run.WorkerId.Value; return Unit.Value; },
                awaiting => { assignedWorker = awaiting.WorkerId.Value; return Unit.Value; },
                comp => { assignedWorker = null; return Unit.Value; },
                failed => { assignedWorker = null; return Unit.Value; },
                suspended => { assignedWorker = suspended.WorkerId.Value; return Unit.Value; },
                retrying => { assignedWorker = null; return Unit.Value; },
                cancelled => { assignedWorker = null; return Unit.Value; }
            );

            cmd.CommandText = @"
                INSERT INTO tasks (id, workflow_state, working_dir, prompt, priority, assigned_worker, state_json, version, created_at, updated_at)
                VALUES (@id, @state, @dir, @prompt, @priority, @assigned_worker, @json, @version, @ts, @ts)
                ON CONFLICT(id) DO UPDATE SET
                    workflow_state = excluded.workflow_state,
                    assigned_worker = excluded.assigned_worker,
                    state_json = excluded.state_json,
                    version = excluded.version,
                    updated_at = excluded.updated_at";
            cmd.Parameters.AddWithValue("@id", taskId.Value);
            cmd.Parameters.AddWithValue("@state", stateType);
            cmd.Parameters.AddWithValue("@dir", state.WorkingDir.Value);

            string prompt = "";
            int priority = 0;
             state.Match(
                 queued => { prompt = queued.Prompt; priority = queued.Priority.Value; return Unit.Value; },
                 disp => { prompt = disp.Prompt; priority = disp.Priority.Value; return Unit.Value; },
                 run => { prompt = run.Prompt; priority = run.Priority.Value; return Unit.Value; },
                 awaiting => { return Unit.Value; },
                 comp => { return Unit.Value; },
                 failed => { return Unit.Value; },
                 suspended => { prompt = suspended.Prompt; priority = suspended.Priority.Value; return Unit.Value; },
                 retrying => { prompt = retrying.Prompt; priority = retrying.Priority.Value; return Unit.Value; },
                 cancelled => { return Unit.Value; }
             );

            cmd.Parameters.AddWithValue("@prompt", prompt);
            cmd.Parameters.AddWithValue("@priority", priority);
            cmd.Parameters.AddWithValue("@assigned_worker", (object?)assignedWorker ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@json", stateJson);
            cmd.Parameters.AddWithValue("@version", version);
            cmd.Parameters.AddWithValue("@ts", nowStr);

            await cmd.ExecuteNonQueryAsync(ct);
            return Result.Ok(true);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<WorkflowState?>> LoadAsync(TaskId taskId, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT state_json FROM tasks WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", taskId.Value);
            var val = await cmd.ExecuteScalarAsync(ct);
            if (val == null) return Result.Ok<WorkflowState?>(null);

            var state = JsonSerializer.Deserialize<WorkflowState>((string)val, JsonOptions);
            return Result.Ok(state);
        }
        catch (Exception ex)
        {
            return new Result<WorkflowState?>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<long>> GetVersionAsync(TaskId taskId, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT version FROM tasks WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", taskId.Value);
            var val = await cmd.ExecuteScalarAsync(ct);
            return Result.Ok(val != null ? (long)val : 0L);
        }
        catch (Exception ex)
        {
            return new Result<long>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<ImmutableArray<TaskId>>> GetActiveTaskIdsAsync(CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM tasks WHERE workflow_state NOT IN ('completed', 'failed', 'cancelled')";
            var builder = ImmutableArray.CreateBuilder<TaskId>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                builder.Add(new TaskId(reader.GetString(0)));
            }
            return Result.Ok(builder.ToImmutable());
        }
        catch (Exception ex)
        {
            return new Result<ImmutableArray<TaskId>>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<int>> GetDispatchableTaskCountAsync(Instant now, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                WITH RECURSIVE task_ancestor(id, ancestor_id, parent_id) AS (
                    SELECT id, id, parent_id FROM tasks
                    UNION ALL
                    SELECT ta.id, t.id, t.parent_id
                    FROM task_ancestor ta
                    JOIN tasks t ON ta.parent_id = t.id
                ),
                task_roots AS (
                    SELECT id, ancestor_id AS root_id FROM task_ancestor WHERE parent_id IS NULL
                )
                SELECT COUNT(*) FROM tasks t
                JOIN task_roots tr ON t.id = tr.id
                WHERE t.workflow_state = 'queued'
                  AND (
                    NOT EXISTS (
                      SELECT 1 FROM leases l
                      WHERE l.working_dir = t.working_dir
                        AND l.expires_at > @now
                    )
                    OR EXISTS (
                      SELECT 1 FROM leases l
                      WHERE l.working_dir = t.working_dir
                        AND l.expires_at > @now
                        AND l.root_task_id = tr.root_id
                    )
                  )";
            cmd.Parameters.AddWithValue("@now", now.Value.ToString("O"));
            var val = await cmd.ExecuteScalarAsync(ct);
            return Result.Ok(val != null ? Convert.ToInt32(val) : 0);
        }
        catch (Exception ex)
        {
            return new Result<int>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<ImmutableArray<WorkflowState>>> ListTasksAsync(CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT state_json FROM tasks ORDER BY created_at DESC";
            var builder = ImmutableArray.CreateBuilder<WorkflowState>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var state = JsonSerializer.Deserialize<WorkflowState>(reader.GetString(0), JsonOptions);
                if (state != null)
                {
                    builder.Add(state);
                }
            }
            return Result.Ok(builder.ToImmutable());
        }
        catch (Exception ex)
        {
            return new Result<ImmutableArray<WorkflowState>>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<WorkflowState?>> LoadByWorkerAsync(WorkerId workerId, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT state_json FROM tasks WHERE assigned_worker = @worker AND workflow_state IN ('running', 'dispatched')";
            cmd.Parameters.AddWithValue("@worker", workerId.Value);
            var val = await cmd.ExecuteScalarAsync(ct);
            if (val == null || val == DBNull.Value) return Result.Ok<WorkflowState?>(null);

            var state = JsonSerializer.Deserialize<WorkflowState>((string)val, JsonOptions);
            return Result.Ok(state);
        }
        catch (Exception ex)
        {
            return new Result<WorkflowState?>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<ImmutableArray<WorkerHandle>>> GetActiveWorkersAsync(CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT custom_id, provider_batch_ref FROM workers WHERE status IN ('submitted', 'inflight')";
            var builder = ImmutableArray.CreateBuilder<WorkerHandle>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string id = reader.GetString(0);
                string? batchRef = reader.IsDBNull(1) ? null : reader.GetString(1);
                builder.Add(new WorkerHandle(new WorkerId(id), new ProviderBatchRef(batchRef ?? "")));
            }
            return Result.Ok(builder.ToImmutable());
        }
        catch (Exception ex)
        {
            return new Result<ImmutableArray<WorkerHandle>>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<bool>> UpsertWorkerAsync(
        WorkerId workerId, ProviderBatchRef batchRef, WorkerStatus status, Usage usage,
        CancellationToken ct = default, string? model = null)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            string nowStr = _clock.Now.Value.ToString("O");
            string statusStr = status.GetType().Name.ToLowerInvariant();

            cmd.CommandText = @"
                INSERT INTO workers (custom_id, provider_batch_ref, status, model, input_tokens, output_tokens, cost_usd, created_at, updated_at)
                VALUES (@id, @batch, @status, @model, @in, @out, @cost, @ts, @ts)
                ON CONFLICT(custom_id) DO UPDATE SET
                    provider_batch_ref = excluded.provider_batch_ref,
                    status = excluded.status,
                    model = COALESCE(excluded.model, workers.model),
                    input_tokens = excluded.input_tokens,
                    output_tokens = excluded.output_tokens,
                    cost_usd = excluded.cost_usd,
                    updated_at = excluded.updated_at";
            cmd.Parameters.AddWithValue("@id", workerId.Value);
            cmd.Parameters.AddWithValue("@batch", string.IsNullOrEmpty(batchRef.Value) ? DBNull.Value : batchRef.Value);
            cmd.Parameters.AddWithValue("@status", statusStr);
            cmd.Parameters.AddWithValue("@model", (object?)model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@in", usage.InputTokens);
            cmd.Parameters.AddWithValue("@out", usage.OutputTokens);
            cmd.Parameters.AddWithValue("@cost", (double)usage.CostUsd);
            cmd.Parameters.AddWithValue("@ts", nowStr);

            await cmd.ExecuteNonQueryAsync(ct);
            return Result.Ok(true);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<string?>> GetWorkerModelAsync(WorkerId workerId, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT model FROM workers WHERE custom_id = @id";
            cmd.Parameters.AddWithValue("@id", workerId.Value);
            var val = await cmd.ExecuteScalarAsync(ct);
            if (val == null || val == DBNull.Value)
            {
                return Result.Ok<string?>(null);
            }
            return Result.Ok<string?>((string)val);
        }
        catch (Exception ex)
        {
            return new Result<string?>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<ProviderBatchRef?>> GetWorkerBatchRefAsync(WorkerId workerId, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT provider_batch_ref FROM workers WHERE custom_id = @id";
            cmd.Parameters.AddWithValue("@id", workerId.Value);
            var val = await cmd.ExecuteScalarAsync(ct);
            if (val == null || val == DBNull.Value)
            {
                return Result.Ok<ProviderBatchRef?>(null);
            }
            return Result.Ok<ProviderBatchRef?>(new ProviderBatchRef((string)val));
        }
        catch (Exception ex)
        {
            return new Result<ProviderBatchRef?>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    // ── IOutbox Implementation ──────────────────────────────────────────

    public async Task<Result<bool>> EnqueueAsync(ImmutableArray<Command> commands, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var txn = conn.BeginTransaction();
            string nowStr = _clock.Now.Value.ToString("O");

            foreach (var cmd in commands)
            {
                string cmdType = cmd.GetType().Name;
                string payload = JsonSerializer.Serialize<Command>(cmd, JsonOptions);
                string cmdId = Guid.NewGuid().ToString("N");
                string idemKey = cmdId;

                cmd.Match(
                    submit => { idemKey = submit.IdemKey.Value; return Unit.Value; },
                    emit => { idemKey = $"stream_{emit.Envelope.TaskId.Value}_{emit.Envelope.Seq}"; return Unit.Value; },
                    schedule => { idemKey = $"retry_{schedule.TaskId.Value}_{schedule.Attempt}"; return Unit.Value; },
                    requeue => { idemKey = $"requeue_{requeue.TaskId.Value}"; return Unit.Value; },
                    release => { idemKey = $"release_{release.TaskId.Value}"; return Unit.Value; },
                    cancel => { idemKey = $"cancel_{cancel.WorkerId.Value}"; return Unit.Value; }
                );

                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = txn;
                insertCmd.CommandText = @"
                    INSERT INTO outbox (id, task_id, command_type, payload_json, idempotency_key, status, attempts, next_attempt_at, created_at)
                    VALUES (@id, 'standalone', @type, @payload, @idem, 'pending', 0, @ts, @ts)
                    ON CONFLICT(idempotency_key) DO NOTHING";
                insertCmd.Parameters.AddWithValue("@id", cmdId);
                insertCmd.Parameters.AddWithValue("@type", cmdType);
                insertCmd.Parameters.AddWithValue("@payload", payload);
                insertCmd.Parameters.AddWithValue("@idem", idemKey);
                insertCmd.Parameters.AddWithValue("@ts", nowStr);
                await insertCmd.ExecuteNonQueryAsync(ct);
            }
            txn.Commit();
            return Result.Ok(true);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<ImmutableArray<OutboxEntry>>> DequeueAsync(int maxBatchSize, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var txn = conn.BeginTransaction();

            string nowStr = _clock.Now.Value.ToString("O");
            var entries = ImmutableArray.CreateBuilder<OutboxEntry>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = @"
                    SELECT id, task_id, payload_json FROM outbox
                    WHERE status = 'pending' AND next_attempt_at <= @now
                    LIMIT @limit";
                cmd.Parameters.AddWithValue("@now", nowStr);
                cmd.Parameters.AddWithValue("@limit", maxBatchSize);

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string id = reader.GetString(0);
                    string taskId = reader.GetString(1);
                    string payload = reader.GetString(2);

                    var command = JsonSerializer.Deserialize<Command>(payload, JsonOptions);
                    if (command != null)
                    {
                        entries.Add(new OutboxEntry(new CommandId(id), new TaskId(taskId), command));
                    }
                }
            }

            // Lock them
            foreach (var entry in entries)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = txn;
                cmd.CommandText = "UPDATE outbox SET status = 'dispatched', dispatched_at = @now WHERE id = @id";
                cmd.Parameters.AddWithValue("@now", nowStr);
                cmd.Parameters.AddWithValue("@id", entry.Id.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            txn.Commit();
            return Result.Ok(entries.ToImmutable());
        }
        catch (Exception ex)
        {
            return new Result<ImmutableArray<OutboxEntry>>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<bool>> AcknowledgeAsync(ImmutableArray<CommandId> commandIds, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var txn = conn.BeginTransaction();

            foreach (var id in commandIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = txn;
                cmd.CommandText = "DELETE FROM outbox WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            txn.Commit();
            return Result.Ok(true);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    // ── IJobStore Implementation ────────────────────────────────────────

    public async Task<Result<bool>> ScheduleAsync(JobDescriptor job, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO jobs (id, task_id, working_dir, job_type, scheduled_at, max_retries, attempt, status)
                VALUES (@id, @task_id, @dir, @type, @sched, @max, @attempt, 'pending')";
            cmd.Parameters.AddWithValue("@id", job.JobId);
            cmd.Parameters.AddWithValue("@task_id", (object?)job.TaskId?.Value ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@dir", ""); // Fill if needed
            cmd.Parameters.AddWithValue("@type", job.JobType);
            cmd.Parameters.AddWithValue("@sched", job.ScheduledAt.Value.ToString("O"));
            cmd.Parameters.AddWithValue("@max", job.MaxRetries);
            cmd.Parameters.AddWithValue("@attempt", job.Attempt);
            await cmd.ExecuteNonQueryAsync(ct);
            return Result.Ok(true);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<ImmutableArray<JobDescriptor>>> FetchDueAsync(Instant now, int maxBatchSize, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, task_id, job_type, scheduled_at, max_retries, attempt FROM jobs
                WHERE status = 'pending' AND scheduled_at <= @now
                LIMIT @limit";
            cmd.Parameters.AddWithValue("@now", now.Value.ToString("O"));
            cmd.Parameters.AddWithValue("@limit", maxBatchSize);

            var builder = ImmutableArray.CreateBuilder<JobDescriptor>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string id = reader.GetString(0);
                string? taskIdStr = reader.IsDBNull(1) ? null : reader.GetString(1);
                string type = reader.GetString(2);
                DateTimeOffset sched = DateTimeOffset.Parse(reader.GetString(3));
                int maxRetries = reader.GetInt32(4);
                int attempt = reader.GetInt32(5);

                builder.Add(new JobDescriptor(
                    id, type, taskIdStr != null ? new TaskId(taskIdStr) : null,
                    new Instant(sched), maxRetries, attempt));
            }
            return Result.Ok(builder.ToImmutable());
        }
        catch (Exception ex)
        {
            return new Result<ImmutableArray<JobDescriptor>>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<bool>> CompleteAsync(string jobId, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE jobs SET status = 'completed' WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", jobId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Result.Ok(true);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<bool>> FailAsync(string jobId, Error error, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE jobs SET status = 'failed', attempt = attempt + 1 WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", jobId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Result.Ok(true);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<bool>> CancelAsync(string jobId, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE jobs SET status = 'cancelled' WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", jobId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Result.Ok(true);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    // ── ILeaseManager Implementation ─────────────────────────────────────

    private async Task<string?> GetRootTaskIdAsync(string taskId, SqliteConnection conn, SqliteTransaction txn, CancellationToken ct)
    {
        string currentId = taskId;
        while (true)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = "SELECT parent_id FROM tasks WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", currentId);
            var val = await cmd.ExecuteScalarAsync(ct);
            if (val == null || val == DBNull.Value)
            {
                return currentId;
            }
            currentId = (string)val;
        }
    }

    public async Task<Result<LeaseDisposition>> AcquireAsync(WorkingDir workingDir, TaskId taskId, TimeSpan ttl, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var txn = conn.BeginTransaction();

            string nowStr = _clock.Now.Value.ToString("O");
            string expiresStr = _clock.Now.Value.Add(ttl).ToString("O");

            // Look up existing lease
            string? existingRootTaskId = null;
            string? existingExpiresStr = null;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = "SELECT root_task_id, expires_at FROM leases WHERE working_dir = @dir";
                cmd.Parameters.AddWithValue("@dir", workingDir.Value);
                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    existingRootTaskId = reader.GetString(0);
                    existingExpiresStr = reader.GetString(1);
                }
            }

            string targetRootTaskId = await GetRootTaskIdAsync(taskId.Value, conn, txn, ct) ?? taskId.Value;

            if (existingRootTaskId == null)
            {
                // No lease exists, acquire fresh
                using var cmd = conn.CreateCommand();
                cmd.Transaction = txn;
                cmd.CommandText = @"
                    INSERT INTO leases (working_dir, root_task_id, holder, expires_at)
                    VALUES (@dir, @root, @holder, @expires)";
                cmd.Parameters.AddWithValue("@dir", workingDir.Value);
                cmd.Parameters.AddWithValue("@root", targetRootTaskId);
                cmd.Parameters.AddWithValue("@holder", taskId.Value);
                cmd.Parameters.AddWithValue("@expires", expiresStr);
                await cmd.ExecuteNonQueryAsync(ct);

                txn.Commit();
                return Result.Ok<LeaseDisposition>(new LeaseDisposition.Acquired(new LeaseRef(targetRootTaskId)));
            }

            DateTimeOffset expires = DateTimeOffset.Parse(existingExpiresStr!);
            if (expires <= _clock.Now.Value)
            {
                // Expired lease, overwrite
                using var cmd = conn.CreateCommand();
                cmd.Transaction = txn;
                cmd.CommandText = @"
                    UPDATE leases
                    SET root_task_id = @root, holder = @holder, expires_at = @expires
                    WHERE working_dir = @dir";
                cmd.Parameters.AddWithValue("@dir", workingDir.Value);
                cmd.Parameters.AddWithValue("@root", targetRootTaskId);
                cmd.Parameters.AddWithValue("@holder", taskId.Value);
                cmd.Parameters.AddWithValue("@expires", expiresStr);
                await cmd.ExecuteNonQueryAsync(ct);

                txn.Commit();
                return Result.Ok<LeaseDisposition>(new LeaseDisposition.Acquired(new LeaseRef(targetRootTaskId)));
            }

            // Active lease exists, check if part of the same task tree
            if (existingRootTaskId == targetRootTaskId)
            {
                txn.Commit();
                return Result.Ok<LeaseDisposition>(new LeaseDisposition.Joined(new LeaseRef(existingRootTaskId)));
            }

            // Blocked by another tree
            txn.Commit();
            return Result.Ok<LeaseDisposition>(new LeaseDisposition.Blocked(new TaskId(existingRootTaskId)));
        }
        catch (Exception ex)
        {
            return new Result<LeaseDisposition>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<bool>> RenewAsync(LeaseRef leaseRef, TimeSpan ttl, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            string expiresStr = _clock.Now.Value.Add(ttl).ToString("O");
            cmd.CommandText = "UPDATE leases SET expires_at = @expires WHERE root_task_id = @root";
            cmd.Parameters.AddWithValue("@expires", expiresStr);
            cmd.Parameters.AddWithValue("@root", leaseRef.Value);
            int rows = await cmd.ExecuteNonQueryAsync(ct);
            return Result.Ok(rows > 0);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<bool>> ReleaseAsync(LeaseRef leaseRef, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM leases WHERE root_task_id = @root";
            cmd.Parameters.AddWithValue("@root", leaseRef.Value);
            int rows = await cmd.ExecuteNonQueryAsync(ct);
            return Result.Ok(rows > 0);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<bool>> IsHeldAsync(LeaseRef leaseRef, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            string nowStr = _clock.Now.Value.ToString("O");
            cmd.CommandText = "SELECT COUNT(*) FROM leases WHERE root_task_id = @root AND expires_at > @now";
            cmd.Parameters.AddWithValue("@root", leaseRef.Value);
            cmd.Parameters.AddWithValue("@now", nowStr);
            var val = await cmd.ExecuteScalarAsync(ct);
            return Result.Ok(val != null && (long)val > 0);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<TaskId?>> GetParentTaskIdAsync(TaskId taskId, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT parent_id FROM tasks WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", taskId.Value);
            var val = await cmd.ExecuteScalarAsync(ct);
            if (val == null || val == DBNull.Value) return Result.Ok<TaskId?>(null);
            return Result.Ok<TaskId?>(new TaskId((string)val));
        }
        catch (Exception ex)
        {
            return new Result<TaskId?>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<bool>> RecordToolCallAsync(TaskId taskId, string toolName, string argumentsJson, string resultJson, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO task_transcripts (task_id, tool_name, arguments_json, result_json, created_at)
                VALUES (@task_id, @tool_name, @arguments_json, @result_json, @created_at)";
            cmd.Parameters.AddWithValue("@task_id", taskId.Value);
            cmd.Parameters.AddWithValue("@tool_name", toolName);
            cmd.Parameters.AddWithValue("@arguments_json", argumentsJson);
            cmd.Parameters.AddWithValue("@result_json", resultJson);
            cmd.Parameters.AddWithValue("@created_at", _clock.Now.Value.ToString("O"));
            int rows = await cmd.ExecuteNonQueryAsync(ct);
            return Result.Ok(rows > 0);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<ImmutableArray<ToolCallRecord>>> GetToolCallsAsync(TaskId taskId, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT tool_name, arguments_json, result_json FROM task_transcripts
                WHERE task_id = @task_id
                ORDER BY id ASC";
            cmd.Parameters.AddWithValue("@task_id", taskId.Value);
            var builder = ImmutableArray.CreateBuilder<ToolCallRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                builder.Add(new ToolCallRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            return Result.Ok(builder.ToImmutable());
        }
        catch (Exception ex)
        {
            return new Result<ImmutableArray<ToolCallRecord>>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }

    public async Task<Result<(Checkpoint Checkpoint, ImmutableArray<ChildResult> Results)?>> GetResumeDataAsync(TaskId taskId, CancellationToken ct = default)
    {
        try
        {
            using var conn = CreateConnection();
            
            // 1. Get last Checkpoint from AwaitRequested
            Checkpoint? checkpoint = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT payload_json FROM events
                    WHERE task_id = @task_id AND type = 'AwaitRequested'
                    ORDER BY seq DESC LIMIT 1";
                cmd.Parameters.AddWithValue("@task_id", taskId.Value);
                var val = await cmd.ExecuteScalarAsync(ct);
                if (val != null && val != DBNull.Value)
                {
                    var evt = JsonSerializer.Deserialize<WorkflowEvent>((string)val, JsonOptions);
                    if (evt is WorkflowEvent.AwaitRequested awaitReq)
                    {
                        checkpoint = awaitReq.Checkpoint;
                    }
                }
            }

            if (checkpoint == null) return Result.Ok<(Checkpoint Checkpoint, ImmutableArray<ChildResult> Results)?>(null);

            // 2. Get last Results from AwaitResolved
            var results = ImmutableArray<ChildResult>.Empty;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT payload_json FROM events
                    WHERE task_id = @task_id AND type = 'AwaitResolved'
                    ORDER BY seq DESC LIMIT 1";
                cmd.Parameters.AddWithValue("@task_id", taskId.Value);
                var val = await cmd.ExecuteScalarAsync(ct);
                if (val != null && val != DBNull.Value)
                {
                    var evt = JsonSerializer.Deserialize<WorkflowEvent>((string)val, JsonOptions);
                    if (evt is WorkflowEvent.AwaitResolved awaitRes)
                    {
                        results = awaitRes.Results;
                    }
                }
            }

            return Result.Ok<(Checkpoint Checkpoint, ImmutableArray<ChildResult> Results)?>((checkpoint, results));
        }
        catch (Exception ex)
        {
            return new Result<(Checkpoint Checkpoint, ImmutableArray<ChildResult> Results)?>.Err(new Error.Permanent("DB_ERROR", ex.Message));
        }
    }
}
