# CrockCode 🍲

**CrockCode** (like a crockpot — slow cooking) is a headless-first CLI coding agent designed to run coding tasks asynchronously using **Anthropic's Message Batches API** in combination with the **Model Context Protocol (MCP)**. 

By utilizing Anthropic's message batches, CrockCode delivers **~50% cheaper inference** in exchange for higher latency (minutes to hours). It is designed for developers who want to queue up large-scale refactoring, codebase search, test-writing, or boilerplate generation tasks and let them run in the background at a fraction of the cost of synchronous agents.

---

## Why CrockCode?

Traditional AI coding assistants run synchronously. Every tool call involves waiting on the API, costing full price, and blocking your terminal. For massive, complex refactoring tasks, this gets expensive and tedious. 

CrockCode inverts this model:
1. **Cost-Efficient**: Leverages the Batch API discount (~50% off standard token pricing).
2. **True Background Agent**: One batch request = one full agent run. The model fetches the task from a custom MCP queue, then drives multiple sequential tool calls (read/edit/grep/bash) executed locally on your machine via a secure public tunnel.
3. **Robust & Resilient**: Built around a durable, event-sourced workflow engine. If your internet drops, the tunnel restarts, or the daemon crashes, CrockCode resumes exactly where it left off without losing state.

---

## Key Features

- **Durable Event-Sourced Workflows**: Built on SQLite with WAL mode. Reconciles state on restart by replaying events.
- **Bearer Token Scoping**: All tool actions are securely bound to the active task and directory using a per-worker authorization token passed via headers, ensuring the model never sees or leaks directory paths.
- **Async Subagents**: Supports spawning child agents asynchronously. The parent agent suspends, harvests its conversation checkpoint, and resumes when children finish.
- **Dynamic Cost Accounting**: Computes real-time costs for Claude Sonnet, Haiku, and Opus models with batch discount rules applied.
- **MCP Gateway Proxy**: Automatically forwards tool requests to custom third-party MCP servers configured via a `--mcp-config` JSON file.
- **Auto-Memory**: Automatically reads and appends instructions from local and user-level `CLAUDE.md` files to the task prompt.
- **Idle Auto-Shutdown**: The daemon monitors active tasks and workers, shutting down gracefully after a configurable idle period (default 5 minutes) to conserve local resources.
- **Provider Agnostic**: Core components are strictly decoupled from vendor SDKs, including an `IBatchProvider` seam with compile-time stubs ready for future OpenAI Batch API support.

---

## Architecture

CrockCode uses a functional-core/imperative-shell design pattern split across modular assemblies:
- **`CrockCode.Core`**: The pure, immutable domain logic, record structures, and service interfaces. Strictly decoupled from database, provider, and protocol libraries.
- **`CrockCode.Engine`**: The workflow runner executing transitions and managing outbox command dispatches.
- **`CrockCode.Storage`**: SQLite implementation of event and projection stores.
- **`CrockCode.Providers`**: Vendor adapter implementations (Anthropic API client and OpenAI stub).
- **`CrockCode.McpServer`**: ASP.NET Core endpoint serving MCP tools (`get_task`, `complete_task`, `Read`, `Write`, `Edit`, `Bash`, `Glob`, `Grep`, etc.).
- **`CrockCode.Coordinator`**: Background daemon manager hosting the public-facing MCP server (secured by cloudflared/ngrok tunnels) and local loopback IPC endpoints.
- **`CrockCode.Cli`**: A CLI application built on `System.CommandLine` and `Spectre.Console` for queue submission and daemon interaction.

---

## Getting Started

### Prerequisites

- .NET 9 SDK
- `cloudflared` or `ngrok` (if using automated tunneling)
- An Anthropic API Key

### Installation

Clone the repository and build:

```bash
git clone https://github.com/AdamFrisby/CrockCode.git
cd CrockCode
dotnet build -c Release
```

### Configuration

Create or modify your configuration at `~/.crockcode/config.json`:

```json
{
  "anthropic_api_key": "your-anthropic-api-key",
  "tunnel_provider": "cloudflared",
  "provider": "anthropic",
  "model": "claude-3-5-sonnet-20241022",
  "max_concurrency": 4,
  "idle_timeout_seconds": 300
}
```

---

## Usage

### 1. Perform a Diagnostics Check
Validate your environment, credentials, and daemon connectivity:
```bash
crock doctor
```

### 2. Submit a Task
Submit a task and follow it in the terminal:
```bash
crock submit -p "Refactor the logging system to use Serilog across the src/ directory"
```
*Note: You can safely detach (Ctrl+C) at any time. The task continues in the background.*

### 3. CLI Command Guide

- **Submit Tasks**:
  ```bash
  crock submit -p "<prompt>" [-C <working-dir>] [--priority <n>]
  ```
- **Manage Tasks**:
  ```bash
  crock list                  # List all tasks and states
  crock status <task-id>      # Check current task details
  crock follow <task-id>      # Stream live progress events
  crock cancel <task-id>      # Cancel a task in progress
  ```
- **Manage Coordinator Daemon**:
  ```bash
  crock daemon start          # Launch daemon in the background
  crock daemon stop           # Stop the running daemon
  crock daemon status         # Query daemon process state
  crock daemon logs           # Tail daemon log output
  ```

---

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.