# CrockCode — Implementation Plan

## Context

**CrockCode** (Crock as in crockpot — slow cooking) is a headless-first CLI coding agent that runs coding work over **Anthropic's Message Batches API + the MCP connector** to get ~50% cheaper inference, accepting high latency (minutes–hours) in exchange. The bet: do the work when it's cheapest, even if it's slower overall.

The architecture hinges on one confirmed-but-unusual capability: **the Batch API supports the `mcp_servers` connector**, and *the batch worker runs the same server-side agentic loop as the sync Messages API* (Anthropic docs, beta header `mcp-client-2025-11-20`). So **one batch request = one full agent run**: the model fetches a task, then drives many sequential MCP tool calls (read/edit/grep/bash…) that Anthropic's infra executes by calling *our* public MCP endpoint over HTTPS, continuing until the model stops.

This **inverts normal control flow**: CrockCode is not "the agent" — it is (a) a passive **MCP tool server** the model reaches into, plus (b) a **batch lifecycle + pool manager**. Every design choice follows from that inversion.

### Goals
- Headless-first CLI (`crock -p "..."`), Claude-Code/Codex-style `--output-format text|json|stream-json`.
- A shared **task queue**; generic batch "worker" requests (system prompt: *"Fetch your first task from MCP"*) that pull tasks at execution time.
- A **pool** of pre-submitted in-flight workers so batch capacity is grabbed as it frees up.
- Queue + pool **shared across multiple CrockCode instances**, with collective pool-size control (coordinator daemon + thin CLI clients).
- Reuse server-side tools (web_search, web_fetch, code_execution) — do **not** reimplement them in MCP.
- Provider seam for **future OpenAI** batch+MCP support.

### Key product decisions (from the user)
- **Working-directory model, no git worktrees.** Each instance runs in its own directory; we do *not* solve concurrent-edit-in-one-dir (same as you don't run two Claude Codes in one dir). Edits are applied **in place**; the user reviews with their own `git diff`. CrockCode does no branching/merging.
- **Concurrency = across directories, serialized within a directory.** At most one running task per working directory; many directories run in parallel up to the global cap.
- **Command execution:** capped `bash` (hard-capped under the MCP timeout, ~50s) **plus** an async job pattern (`start_job`/`poll_job`) for long builds/tests.
- **Public ingress:** support **both** an auto-managed tunnel (cloudflared/ngrok) *and* a user-supplied stable HTTPS URL.
- Plan covers the **full phased system**; implementation starts at Phase 1.

---

## Engineering standards (cross-cutting, non-negotiable)
These shape every phase below; treat them as acceptance criteria, not aspirations.

1. **Loose coupling, provider-agnostic, contract-first.** Every external dependency and every swappable concern sits behind an interface in `CrockCode.Core`; concrete implementations live in edge projects and are wired by DI only. No domain/application code references a vendor type. Core contracts include: `IBatchProvider` (Anthropic now, OpenAI later), `ITaskStore`/`IEventStore`/`IJobStore`, `ITunnelProvider`, `IWorkspaceResolver`, `IMcpToolHost`, `IControlChannel`, `IClock`, `ITokenSigner`, `ILeaseManager`. Provider-specific quirks (beta headers, batch lifecycle words) are mapped to neutral domain concepts at the boundary.
2. **Functional core / imperative shell (F#-style in C#).** The domain is **immutable**: positional `record`s, `init`-only members, immutable collections (`ImmutableArray`/`ImmutableDictionary`). State changes are **pure functions returning new values** — never in-place mutation. Model variant states as closed unions (sealed `abstract record` hierarchies or a discriminated-union helper) and drive logic with exhaustive `switch` pattern-matching. Expected/transitory failures are returned as `Result<T, Error>` (an explicit `Ok|Err` union), **not** thrown; exceptions are reserved for truly exceptional/programmer errors. All I/O (SQLite, HTTP, process, tunnel, clock, randomness) is pushed to the shell behind the interfaces above so the core is deterministic and unit-testable without mocks-of-mocks.
3. **Determinism & testability.** No ambient `DateTime.Now`/`Guid.NewGuid()`/`Random` in core — inject `IClock`, an id factory, and a seedable RNG. This is also what makes the workflow engine replayable.

---

## Durable workflow engine (the spine of the system)
Every task is a **durable, resilient, resumable workflow**, not an ad-hoc set of status flags. This generalizes the earlier pool/reconciler description into a first-class engine.

**Model.** A task's lifecycle is an explicit state machine expressed as an immutable union:
`Queued → Dispatched → Running → AwaitingResult → (Completed | Failed | Cancelled)`, with `Retrying` as a transient sub-state. The transition function is **pure**:
```
decide : (WorkflowState, WorkflowEvent, EngineContext) -> (WorkflowState, Command[])
```
It takes the current persisted state + an inbound event (e.g. `WorkerClaimed`, `ToolProgress`, `CompleteRequested`, `BatchEnded`, `BatchExpired`, `TransientError`, `Tick`) and returns the **next immutable state plus a list of side-effect Commands** (`SubmitWorker`, `RecordEvent`, `ReleaseTask`, `ScheduleRetry`, `Requeue`, `EmitStreamEvent`). The core decides; the shell executes commands and persists.

**Durability & resumability.** State is the source of truth in SQLite and every transition is committed **transactionally with the event that caused it** (event-sourced: `events` is the append-only log, `tasks`/`workers` are projections). On daemon crash/restart the **Reconciler rehydrates** each non-terminal workflow from its persisted state and last event, re-queries provider batch status, and resumes by feeding the appropriate event into `decide` — no work is lost (batches durable 29 days on Anthropic's side). Because `decide` is pure and deterministic, resume == replay.

**Resilience / transitory errors.** Errors are classified at the boundary into `Transient` (network blip, 429, provider 5xx, MCP timeout, tunnel flap) vs `Permanent` (auth failure, invalid request, poison task). Transients drive `ScheduleRetry` with **exponential backoff + jitter**, bounded by `max_attempts`; the retry schedule itself is persisted so backoff survives restarts. Permanent errors transition to `Failed` with a reason. **Idempotency keys** (worker `custom_id`, task id, command id) make every command safe to re-execute after a crash mid-flush. Per-directory **leases** (`ILeaseManager`, persisted with expiry) enforce the single-running-task-per-directory rule even across daemon restarts and multiple instances.

**Why this matters here specifically.** The batch+MCP model is inherently long-latency and partial-failure-prone (hours-long runs, ephemeral tunnels, async callbacks). A durable workflow engine is the natural fit: it makes "submit cheap work, walk away, survive restarts, resume exactly" a property of the architecture rather than something bolted on.

---

## Confirmed technical constraints (research)
- Batch + MCP works; beta header `mcp-client-2025-11-20`. Batch endpoints: `POST/GET /v1/messages/batches`, results retained 29 days, processing usually <1h (24h hard cap), up to 100k req/batch, processing-queue caps per tier (Tier 1: 100k). Use `custom_id` to match results; order not guaranteed.
- MCP connector requires a **public HTTPS** URL (Streamable HTTP), supports `authorization_token` bearer.
- **MCP tool responses must return fast** (~60s timeout, undocumented). Therefore: `get_task` returns immediately (task or `no_task`); no server-side blocking; long commands go through async jobs.
- Reuse server-side tools: `web_search_*`, `web_fetch_*`, `code_execution_*`. Note `code_execution`'s sandbox is **not** our directory — all real edits go through our file tools.
- OpenAI batch does **not** yet support MCP — Anthropic-only for now; keep a provider seam.

---

## Tech stack (.NET 9 / C#)
| Concern | Choice |
|---|---|
| MCP server (Streamable HTTP) | `ModelContextProtocol.AspNetCore` (official) — `[McpServerTool]`, `MapMcp()` |
| Anthropic client | Official `Anthropic` C# SDK (`Messages.Batches.*`); fall back to `HttpClient` for any beta-param gaps |
| CLI | `System.CommandLine` (parsing) + `Spectre.Console` (human TTY rendering) |
| JSON / stream-json | `System.Text.Json` (source-gen), newline-delimited |
| Shared state | `Microsoft.Data.Sqlite`, WAL, single writer = daemon |
| Tunnel | `cloudflared`/`ngrok` child process via a `TunnelManager` abstraction (provider plug-ins) |
| Local IPC (CLI↔daemon) | Loopback HTTP/2 (minimal API + SSE for event stream) on the daemon's Kestrel host |
| Git | **None required** (working-dir model). Optionally shell out to `git diff` for result summaries only. |

---

## Project layout
```
CrockCode.sln
 ├─ CrockCode.Core         // PURE functional core, zero I/O deps:
 │                         // immutable domain records (Task, Worker, Workspace, PoolConfig,
 │                         // WorkflowState union, WorkflowEvent union, Command union, Result<T,Error>);
 │                         // the workflow `decide` transition function (pure);
 │                         // ALL contracts: IBatchProvider, ITaskStore/IEventStore/IJobStore,
 │                         // ITunnelProvider, IWorkspaceResolver, IMcpToolHost, IControlChannel,
 │                         // ILeaseManager, IClock, ITokenSigner, id/RNG factories
 ├─ CrockCode.Engine       // imperative shell: WorkflowRunner executes Commands from `decide`,
 │                         // Reconciler (rehydrate+resume), retry/backoff scheduler, lease enforcement
 ├─ CrockCode.Storage      // Sqlite event store (append-only) + projections; migrations; atomic claim
 ├─ CrockCode.Providers    // AnthropicBatchProvider: worker-request builder (system prompt,
 │                         // mcp_servers, beta header, auth token), submit/poll/harvest
 ├─ CrockCode.McpServer    // [McpServerTool] classes: QueueTools, FileTools, SearchTools, ShellTools;
 │                         // WorkspaceScopeFilter (validate workspace_id), path-containment guard
 ├─ CrockCode.Coordinator  // IHost daemon: Kestrel (public MCP + loopback control API),
 │                         // PoolManager, BatchPoller, Reconciler, TunnelManager, control endpoints
 ├─ CrockCode.Cli          // `crock` entrypoint; submit/follow/status/list/result/daemon/pool/config/
 │                         // setup/doctor; daemon auto-spawn + lockfile; text/json/stream-json renderers
 └─ CrockCode.Tests
```
The **Coordinator** composes McpServer + Storage + Providers + Core into one `WebApplication` with two listeners: public MCP (tunneled) and loopback control. The **CLI** references only Core DTOs + the control client (stays thin).

---

## Data model (SQLite, WAL; `busy_timeout=5000`, `synchronous=NORMAL`)
**Event-sourced:** `events` is the append-only source of truth; `tasks`/`workers`/`jobs`/`leases` are projections rebuilt from it. Every workflow transition is committed in **one transaction** with its causing event, so state and history never diverge and resume == replay.
- **`tasks`** (projection): `id` (`tsk_<ulid>`), `workflow_state` (serialized union: `queued`/`dispatched`/`running`/`awaiting_result`/`retrying`/`completed`/`failed`/`cancelled`), `parent_id`, `working_dir` (absolute; the serialization domain), `prompt`, `priority`, `model`, `assigned_worker`, `workspace_token_hmac`, `result_summary`, `diffstat`, `attempts`, `max_attempts`, `next_retry_at`, timestamps.
- **`leases`**: `working_dir` (PK), `task_id`, `holder` (worker/instance), `expires_at` — durable per-directory single-runner enforcement (`ILeaseManager`), survives restarts.
- **`workers`** (one per batch worker request): `custom_id` (`wkr_<ulid>`, PK), `batch_id`, `status` (`submitted`→`in_flight`→`succeeded`/`errored`/`expired`), `mcp_url` (detect stale-URL failures), `input_tokens`/`output_tokens`/`cost_usd`, timestamps.
- **`task_assignments`**: worker↔task over a drain (`worker_custom_id`, `task_id`, `claimed_at`, `released_at`).
- **`pool_config`** (kv or single row): `max_concurrency`, `warm_idle_buffer`, `max_tasks_per_worker`, `model`, `poll_interval`, `mcp_public_url`, `beta_header`.
- **`events`** (append-only, **source of truth** + stream-json replay): `seq` (autoinc PK), `task_id`, `type`, `payload_json`, `ts`. Drives both projection rebuild and the live event stream.
- **`jobs`** (async commands): `id`, `task_id`, `working_dir`, `command`, `pid`, `status`, `stdout`/`stderr` tail, `exit_code`.

**Atomic claim (the linchpin):** `get_task` runs a single `UPDATE tasks SET status='running', assigned_worker=… WHERE id = (SELECT id FROM tasks WHERE status='queued' AND working_dir NOT IN (SELECT working_dir FROM tasks WHERE status='running') ORDER BY priority, created_at LIMIT 1) RETURNING …`. This both claims a task and **enforces per-directory serialization** in one statement, with no in-memory locks.

---

## Worker→task correlation (no sessions, no worktrees)
A worker's many tool calls arrive as independent HTTPS calls. We must map each to a task + directory **without** relying on MCP session affinity (undocumented under batch, breaks across daemon restarts).

**Design — explicit `workspace_id` capability token:**
- `get_task()` returns the task **+ an opaque HMAC-signed `workspace_id`** (`ws_<taskId>_<nonce>`).
- **Every** other tool takes `workspace_id` as a required first arg. Server validates HMAC → resolves `task → working_dir`, stateless and restart-safe.
- File ops resolve paths **relative to `working_dir`**, canonicalized with containment checks (reject `..`/absolute escapes).
- System prompt enforces it with a worked example; server **rejects** missing/invalid/forged tokens or tokens for non-`running` tasks with a structured error restating the rule. (Defense-in-depth — never trust the model.)

System prompt skeleton:
> *You are a CrockCode worker. Call `get_task` (no args). It returns `workspace_id` and a task. Pass that exact `workspace_id` to every later tool call — never invent/omit it. When done, call `complete_task` with `workspace_id` + summary. If `get_task` returns `{"status":"no_task"}`, stop immediately. When `complete_task` succeeds, you may call `get_task` again to drain more work (up to your limit), else stop.*

---

## MCP tool surface
All work tools take `workspace_id` (required, first param) and are sandboxed to `working_dir`.

**Queue/control:** `get_task()`; `enqueue_task(workspace_id, prompt, priority?)` (sets `parent_id`, fan-out); `complete_task(workspace_id, summary, status?)`; `report_progress(workspace_id, message)` (emits a live event).

**Filesystem:** `read_file(ws, path, offset?, limit?)`, `write_file(ws, path, content)`, `edit_file(ws, path, old_string, new_string, replace_all?)`, `multi_edit(ws, path, edits[])`, `glob(ws, pattern)`, `grep(ws, pattern, path?, glob?, output_mode?)` (wrap ripgrep if present), `list_dir(ws, path?)`.

**Command exec:** `bash(ws, command, timeout_ms?)` — hard cap ~50s, runs in `working_dir`; `start_job(ws, command)` → `job_id` + `poll_job(ws, job_id)` → `{status, stdout_tail, exit_code}` for long builds/tests (daemon runs the process detached, model polls across tool calls).

**Deliberately omitted** (server-side tools handle them): `web_search`, `web_fetch`, `code_execution`.

---

## CLI surface & async UX
Binary `crock`. The client is a **thin view over daemon state**; submit writes a task row and (optionally) tails the durable event log — making detach/re-attach trivial.

**Commands:** `submit` (default via `-p`), `follow <task>`, `status [<task>]`, `list`, `logs <task>`, `result <task>`, `diff <task>`, `cancel`, `retry`, `tasks tree <task>`, `daemon start|stop|restart|status|logs`, `pool [--size n] [--max-concurrency n]`, `config get|set|list|path`, `setup`, `doctor`, `tunnel status|url`.

**Key flags:** `-p/--print`, `--output-format text|json|stream-json`, `-C/--cwd`, `-d/--detach`, `-f/--follow`, `--model`, `--priority`, `-j/--json`, `--no-daemon-autostart`, `--since-event <seq>`, `--timeout`.

**Async defaults:** submit is always non-blocking at the queue level.
- Interactive TTY `crock -p "..."` → print task id, then **follow** with progress; **Ctrl-C detaches the view** (task keeps running; print `crock follow <id>`); double Ctrl-C → offer cancel.
- Non-TTY/piped → **detach by default**, emit `{task_id, state:"queued"}`, exit 0.
- `--output-format stream-json` → always follow & stream JSONL until terminal.

A `progress/queued` event carries `position_in_queue`, `pool_slots busy/total`, soft `est_dispatch` so "slow is normal" is legible.

**stream-json** is a superset of Claude Code's schema (keep `system`/`assistant`/`user`/`result` shapes identical) plus additive orchestration types: `task_queued`, `worker_assigned`, `batch_submitted`, `batch_status`, `progress`, `subtask_enqueued`, `report_progress→message`, `error`. Every line carries `task_id`, `seq` (per-task monotonic, enables resume), `ts`. `result` includes `total_cost_usd`, `usage`, `duration_ms`.

---

## Pool manager algorithm (a `Tick` event into the workflow engine; Coordinator `BackgroundService`, ~10–15s)
The pool/poller/reconciler are **drivers that feed events into the pure `decide` function**; they do not contain business logic themselves. The loop below is what the engine decides on `Tick` and provider-status events.
```
dispatchable = count(tasks queued whose working_dir has no running task)   // per-dir serialization
inFlight     = count(workers in {submitted,in_flight})
desired      = min(dispatchable + warm_idle_buffer, max_concurrency)        // collective cap = shared governor
if desired > inFlight: submit (desired - inFlight) generic worker batch requests
// never cancel in-flight workers to shrink: an idle worker does get_task→no_task→stop (≈free)
```
- **Generic workers**, 1 request per batch initially (easy correlation/harvest); micro-batch later (`custom_id` keyed) if volume demands.
- **Draining:** after `complete_task`, system prompt lets a worker `get_task` again up to `max_tasks_per_worker` (default 3; set 1 for max parallelism). Efficiency vs parallelism knob.
- **Polling:** per-batch backoff (fast right after submit, widening to minutes); respect 429 with jittered backoff. On `ended`, harvest `custom_id`→worker, record usage/cost. **Task completion is driven by `complete_task`, not batch end**; a worker batch that `ended` with its task still `running` = failure.
- **Retry/expiry:** `errored`/`expired`/early-stop → `attempts++`; re-queue if `< max_attempts` (default 3) else `failed`. Continuous submission means an expired worker's task is simply re-picked.
- **Reconciliation on startup (crash recovery):** all durable state in SQLite; re-`Retrieve` non-terminal `batch_id`s, harvest `ended`, re-queue tasks whose worker died. Nothing in flight is lost (batches durable 29 days). **Stale-URL caveat:** workers carry the `mcp_url` they were submitted with; a daemon restart with a new ephemeral tunnel URL strands in-flight workers → they fail tool calls → re-queued. Push users toward a stable URL for production.

---

## Process model & onboarding
- **Daemon-centric:** one coordinator hosts the single public MCP endpoint, the SQLite writer, the PoolManager/BatchPoller/Reconciler, and the loopback control API. CLIs are thin clients.
- **Auto-spawn + lockfile election:** CLI discovers the daemon via `~/.crockcode/daemon.json` (`{pid, controlPort, mcpPublicUrl}`); if absent/dead, acquires exclusive `daemon.lock`, spawns `crock daemon --detached`, waits for health, proceeds. `--no-daemon-autostart` opts out. Explicit `crock daemon` runs foreground (systemd-friendly).
- **Tunnel:** `TunnelManager` with providers `cloudflared` | `ngrok` | `manual` (bring-your-own stable URL). Auto providers launch on daemon start, capture the URL, write `runtime.json`; `manual` reads `mcp.public_url`. **Reachability is probed (external round-trip) before any batch is submitted** — a wrong/stale URL otherwise fails silently and asynchronously.
- **Config** precedence (high→low): flags → env (`ANTHROPIC_API_KEY`, `CROCK_MCP_PUBLIC_URL`, `CROCK_HOME`, `CROCK_MODEL`, …) → project `./.crockcode/config.json` → user `~/.crockcode/config.json` → defaults. `~/.crockcode/` holds `config.json`, `queue.db`, `daemon.{json,lock,log}`.
- **`crock setup`** (wizard: key → ingress choice → pool size → model → start daemon → probe) and **`crock doctor`** (non-interactive PASS/WARN/FAIL with remedies + nonzero exit; the high-value check is the external tunnel round-trip).

---

## Phased build

**Phase 1 — Vertical slice (prove the riskiest unknowns).** Single daemon, single task, manual tunnel. Establish the **functional-core/imperative-shell split** and a minimal pure `decide` from day one (immutable records + `Result<T,Error>`, contracts behind `IBatchProvider`/`ITaskStore`/`IWorkspaceResolver`). MCP server with `get_task`/`complete_task` + `read_file`/`write_file`/`edit_file`/`list_dir` scoped to one `working_dir` via `workspace_id`. `AnthropicBatchProvider` submits ONE generic worker (beta header, `mcp_servers`→tunnel, system prompt), polls `Retrieve`, harvests. `crock -p "..."` enqueues → daemon submits worker → model edits a real file → `complete_task` → CLI prints result. **Success = a real batch round-trip mutates a real file via MCP**, validating connector reachability, `workspace_id` scoping, tool latency, and result correlation. (Keep the engine minimal here, but never bolt mutability/vendor types into core — that debt is expensive to unwind.)

**Phase 2 — Tool completeness & safety.** Add `glob`, `grep`, `multi_edit`, capped `bash`; path-containment guard; structured tool errors. Agent can now self-verify with quick tests.

**Phase 3 — Tunnel & onboarding.** `TunnelManager` (cloudflared + ngrok + manual), reachability probe, full config resolution, `crock setup`/`crock doctor`/`crock tunnel`.

**Phase 4 — Durable workflow engine & pool.** Promote the minimal `decide` to the full state machine: `WorkflowState`/`WorkflowEvent`/`Command` unions, event-sourced persistence with transactional state+event commits, Reconciler rehydrate-and-resume, persisted exponential-backoff retry for transient errors, `ILeaseManager` per-directory leases, poison-task `max_attempts`. Pool drivers on top: `max_concurrency`, `warm_idle_buffer`, `no_task` cheap-stop, multi-batch polling/backoff.

**Phase 5 — Multi-instance shared pool.** Daemon auto-spawn + lockfile election, loopback control API, thin CLI client, shared queue across instances/directories, collective `crock pool set-max`.

**Phase 6 — Streaming UX & fan-out.** `events` table + `--output-format stream-json`, follow/detach/re-attach with `--since-event`, `report_progress`, `enqueue_task` subtasks (`tasks tree`), worker draining (`max_tasks_per_worker`), result presentation (summary/diffstat/cost; optional `git diff` capture).

**Phase 7 — Hardening & extensibility.** Async jobs (`start_job`/`poll_job`) for long builds/tests, cost/token accounting & reporting, idle auto-shutdown, `IBatchProvider` OpenAI seam, security review of `bash`/path handling/token validation.

---

## Verification
- **Phase 1 end-to-end (the critical proof):** run `crock daemon` locally; expose via `cloudflared tunnel`; `crock doctor` round-trips the public MCP URL; `crock -p "create hello.txt with 'hi'"` in a scratch dir → confirm a batch is submitted (log the `batch_id`), the worker calls `get_task`/`write_file`/`complete_task`, and `hello.txt` appears on disk. Inspect `usage`/`cost` in the harvested result.
- **MCP server unit/integration:** call each tool over Streamable HTTP with a synthetic `workspace_id`; assert path-containment rejects `..`/absolute escapes and forged/expired tokens.
- **Atomic claim:** concurrent `get_task` calls never hand the same task twice and never run two tasks in one `working_dir` (DB-level test).
- **Pure `decide` (property tests):** the transition function is total and deterministic — same `(state, event)` always yields the same `(state', commands)`; no illegal transitions; `Result` errors never throw. No I/O, no mocks.
- **Workflow durability/resume:** event-source a task partway, drop the in-memory engine, rehydrate projections from `events`, feed the next event → assert it resumes to the identical state a non-crashed run would reach (resume == replay). Transient-error injection → assert persisted backoff schedule survives restart; lease expiry frees a directory.
- **Pool drivers:** with a faked `IBatchProvider`, assert `inFlight` converges to `desired`, respects `max_concurrency`, re-queues on `errored`/`expired`.
- **Provider-agnosticism:** a stub `IBatchProvider` substitutes for Anthropic with zero changes to Core/Engine/Cli (compile-time proof no vendor type leaked).
- **stream-json:** golden-file the example sequence (task that enqueues a subtask); verify `seq` monotonicity and `--since-event` resume.
- **Cost smoke test:** a tiny real task; confirm the 50% batch discount appears in recorded usage vs the sync price.

## Top risks
1. **MCP connector reachability/auth** — make-or-break; front-loaded in Phase 1.
2. **Ephemeral tunnel URL across daemon restarts** strands in-flight workers — re-queue + push stable-URL story.
3. **~60s tool timeout** forbids long `bash` — async jobs (Phase 7); cap `bash` hard.
4. **Session-affinity uncertainty** — mitigated by the `workspace_id` token; never regress to session scoping.
5. **`code_execution` sandbox ≠ our directory** — keep all edits in our file tools.
6. **Non-TTY detach default** diverges from Claude Code's blocking `-p` — documented; `--follow` restores blocking.
