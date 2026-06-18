# agent-sandbox-temporal

A small **.NET 9** service that runs a **multi-agent system** as a **durable, retryable, resumable
Temporal workflow**. The agents solve math / data-analysis tasks by writing Python; the
model-generated code is **untrusted** and runs inside an **isolated, no-network sandbox** (gVisor
`runsc`, with an automatic fallback to a hardened `runc` container). Every workflow step emits a
**Langfuse-style trace**.

Stack: ASP.NET Core minimal API + the official [`Temporalio`](https://github.com/temporalio/sdk-dotnet)
.NET SDK; Anthropic and Langfuse called over HTTP via `HttpClient`. The sandbox image is Python
(numpy/pandas) because that's what the agents generate — the host service is fully .NET.

```
                 ┌──────────────┐   start / query / SSE   ┌───────────────────┐
   client ──────▶│  ASP.NET API │────────────────────────▶│  Temporal server  │
  (HTTP +        │ (minimal API)│                         └─────────┬─────────┘
   multipart)    └──────────────┘                                   │
                                                          worker runs the workflow
                                                                    │
        planner ───────────▶ coder-executor ──────────────▶ analyst
        (Claude)               │       ▲                     (Claude)
                               │       │ error-driven self-correction (≤ MAX_ITERATIONS)
                               ▼       │
                        ┌───────────────────────┐
                        │  sandbox (NO network)  │  ← gVisor runsc / hardened runc
                        │  python + numpy/pandas │
                        └───────────────────────┘
                               │
                 every step → Langfuse trace + trace.jsonl
```

The LLM reasoning (real Anthropic calls) happens **host-side only**, in Temporal activities with
network access. The sandbox never has network access and never sees a secret.

---

## Quickstart

Prerequisites: Docker (with Compose) and an Anthropic API key. (The .NET SDK is only needed for
local `dotnet build`/`test`; the container build compiles everything for you.)

```bash
cp .env.example .env
#   edit .env → set ANTHROPIC_API_KEY

# Bring up Temporal, Langfuse, and the app (api + worker)
# (make up depends on the sandbox target, so it builds the sandbox image automatically)
make up               # docker compose up -d --build

# 2) Start a run
curl -s -X POST http://localhost:8000/runs \
  -F 'task=Compute the eigenvalues of [[2,1],[1,2]] and explain what they mean.' \
  -F 'tenant=acme'
# → {"run_id":"run-abc123...","tenant":"acme","files":[]}

# 3) Stream its status
curl -N http://localhost:8000/runs/run-abc123.../stream
```

With an uploaded dataset:

```bash
curl -s -X POST http://localhost:8000/runs \
  -F 'task=Find the correlation between columns x and y in data.csv.' \
  -F 'tenant=acme' \
  -F 'files=@examples/data.csv'
```

Or just open the built-in **web UI at http://localhost:8000** — enter a task, optionally attach
files, and watch the planner → coder → sandbox → analyst timeline stream live with the result.

UIs: Web UI → http://localhost:8000 · Temporal → http://localhost:8080 · Langfuse → http://localhost:3001
(login `dev@example.com` / `dev-password`) · Open WebUI → http://localhost:3002 (no login).

Open WebUI is wired to the built-in OpenAI-compatible proxy (`openai-proxy`, port 8001) so you can
chat with the planner/coder/analyst agents through a standard chat interface without any API key.

Local dev without Docker for the app: `make build` (`dotnet build`) and `make test` (`dotnet test`)
need the .NET 9 SDK; Temporal/Langfuse still come from compose.

---

## API

`multipart/form-data` so runs can carry optional input files.

| Method | Path                | Body / params                                        | Returns |
|--------|---------------------|------------------------------------------------------|---------|
| POST   | `/runs`             | `task` (str, required), `tenant` (str), `files` (0..N) | `202 {run_id, tenant, files[]}` |
| GET    | `/runs/{id}`        | —                                                    | status snapshot |
| GET    | `/runs/{id}/stream` | —                                                    | SSE stream of status until terminal |
| GET    | `/healthz`          | —                                                    | liveness |

Status is read live from a **Temporal workflow query**, so it survives api/worker restarts. A status
object looks like:

```json
{
  "runId": "run-...", "tenant": "acme", "task": "...",
  "state": "running", "currentStep": "sandbox-exec", "iterations": 2,
  "steps": [
    {"name": "planner", "status": "ok", "iteration": 0, "at": "..."},
    {"name": "coder", "status": "ok", "iteration": 1},
    {"name": "sandbox-exec", "status": "error", "iteration": 1, "detail": "ZeroDivisionError..."},
    {"name": "coder", "status": "ok", "iteration": 2},
    {"name": "sandbox-exec", "status": "running", "iteration": 2}
  ],
  "result": null
}
```

Upload guardrails: per-file (`MAX_FILE_BYTES`) and per-request (`MAX_TOTAL_UPLOAD_BYTES`) size caps,
an extension allowlist (`.csv .tsv .json .txt`), and filename sanitization. Files are
mounted **read-only** into the sandbox at `/work/inputs/`.

---

## Isolation model

The untrusted step is the **execution of model-generated Python**. It runs in an **ephemeral
container, one per attempt**, created and destroyed by `Sandbox.cs` (which shells out to the Docker
CLI). Isolation is layered:

| Control | How | Why |
|---|---|---|
| **No network** | `--network none` | The code cannot exfiltrate data, reach internal services, or call out. The model itself runs host-side, so it loses nothing by the sandbox being offline. |
| **Userspace kernel** | gVisor `runsc` runtime | Syscalls are served by gVisor in userspace, not the host kernel — a small, hardened kernel attack surface instead of the full Linux ABI. |
| **Read-only rootfs** | `--read-only` + `tmpfs /tmp` | The image cannot be modified; only `/tmp` (small, `noexec`) and the per-run `/work/output` are writable. |
| **Non-root** | `--user 65534:65534` | No root inside the container even before namespacing. |
| **No capabilities** | `--cap-drop ALL` | Removes every Linux capability. |
| **No privilege escalation** | `--security-opt no-new-privileges` | setuid binaries can't gain privileges. |
| **Default seccomp** | Docker default profile | Blocks dangerous syscalls. |
| **Resource caps** | `--memory`, `--memory-swap` (= memory, swap off), `--cpus`, `--pids-limit` | Contains fork bombs, memory/CPU exhaustion. |
| **Wall-clock timeout** | process timeout → `docker kill` | Hard kill at `SANDBOX_TIMEOUT_SECONDS`; mirrored by the Temporal `StartToCloseTimeout`. |
| **Minimal image** | python-slim + numpy/pandas only | No shell utilities, no package manager at runtime, no extra binaries. |

### Isolation tiers (strongest → weakest)

```
Firecracker microVM   >   gVisor runsc   >   hardened runc
   (production)             (default)        (local fallback)
```

`SANDBOX_RUNTIME=auto` (default) uses **gVisor `runsc`** if the Docker daemon advertises it, and
otherwise falls back to **hardened `runc`** — same no-network / dropped-caps / read-only / resource
profile, weaker kernel isolation. Force a tier with `SANDBOX_RUNTIME=runsc|runc`.

> **macOS note.** `runsc` only runs on Linux, so on a Mac it would have to run inside Docker's Linux
> VM, and Docker Desktop doesn't ship the gVisor runtime by default. The auto-fallback keeps the
> system runnable on a laptop while preserving the design intent. To exercise real gVisor, run on a
> Linux host with [gVisor installed](https://gvisor.dev/docs/user_guide/install/) and
> `SANDBOX_RUNTIME=runsc`.

### Firecracker production path (documented, not wired locally)

For production multi-tenant isolation, replace the container runtime with a **Firecracker microVM
per execution**:

- A thin **VMM pool** boots microVMs from a minimal kernel + read-only rootfs containing the Python
  runtime; the model script and inputs are injected via a read-only block device or `vsock`.
- The **jailer** runs each Firecracker process in its own chroot/cgroup/namespace under a dedicated
  uid, with seccomp on the VMM itself.
- **No network device** is attached to the guest (or only a tightly filtered tap with default-deny
  egress). I/O (stdin/stdout/artifacts) flows over `vsock`.
- microVMs are **single-use** and destroyed after each run; boot is ~100–150 ms so per-run VMs are
  practical.

Only the execution activity (`Activities.ExecuteInSandbox` → `Sandbox.cs`) changes; the workflow,
agents, API, and tracing are unaffected. `Sandbox.cs` is intentionally the single swap-point.

---

## Retry & timeout strategy

| Step | Timeout | Retries |
|---|---|---|
| `Plan`, `GenerateCode`, `Analyze` (LLM) | `StartToClose = 120s` | exp. backoff (2s→30s), **4 attempts**; **non-retryable** on `AnthropicNonRetryableException` (auth/4xx) |
| `ExecuteInSandbox` | `StartToClose = SANDBOX_TIMEOUT_SECONDS + 30s`, `Heartbeat = 15s` | **2 attempts** (infra-level only) |
| agent self-correction loop | — | logical failures (non-zero exit) feed the error back to the coder, up to `MAX_ITERATIONS` |
| whole run | `WORKFLOW_TIMEOUT_SECONDS` (execution timeout) | backstop |

Two layers, on purpose: **infra retries** (a transient Docker or API hiccup → Temporal re-runs the
activity) are distinct from **logical iteration** (the code was wrong → the *coder agent* rewrites it
using real stderr). The Anthropic client throws a dedicated non-retryable exception type for auth/4xx
so Temporal won't waste attempts on a bad key. The sandbox wall-clock limit is enforced **twice** —
inside the runner (`docker kill`) and by Temporal's `StartToCloseTimeout` — so a wedged container
can't hang a run.

**Durability / resumability** come from Temporal for free: workflow state is the event history, all
side effects are activities, so a worker crash mid-run resumes from the last completed activity with
no duplicated LLM calls or sandbox executions. Kill the `worker` container mid-run and watch it pick
up where it left off.

---

## Tracing

Langfuse-style: **one trace per run** (keyed by `runId`), **one observation per step** — LLM steps as
`GENERATION`s (with model + token usage), the sandbox step as a `SPAN` (with runtime, exit code,
duration, truncated stdout/stderr, `network: none`). Every observation carries a `tenant` tag.

Tracing is **fail-open**: every observation is always written to `.runs/<run_id>/trace.jsonl` and to
stderr, and *additionally* shipped best-effort to Langfuse via its ingestion API. If Langfuse is down
or unconfigured, runs are unaffected and traces are still inspectable on disk.

---

## Multi-tenant production hardening

What this preview does, and what a production deployment should add:

**Isolation & blast radius.** Every run gets a fresh, single-use sandbox with no shared writable
state; no network; non-root; dropped caps; resource caps. Production: move to **Firecracker microVMs**
(above), pin and regularly rebuild the sandbox image, scan it, and run the worker fleet on dedicated
nodes separate from the control plane.

**Secrets.** The Anthropic key lives only in the host-side worker process and is **never** passed into
the sandbox (no env, no mount). Production: load it from a secrets manager, scope per-tenant keys, and
keep the sandbox on a node/identity with no cloud credentials.

**Tenant separation.** Runs are tagged with `tenant` end-to-end (workflow, traces). Production: give
each tenant its own **Temporal namespace** and/or **task queue**, enforce per-tenant **concurrency &
resource quotas** and **rate limits**, and isolate run storage per tenant.

**Input / output safety.** Uploads are size-capped, extension-allowlisted, and name-sanitized; tool
output is truncated (`SANDBOX_OUTPUT_MAX_BYTES`). Production: add content scanning, a max input row
count, and per-tenant storage encryption + lifecycle/TTL on `/.runs`.

**Egress.** Sandbox egress is default-deny (`--network none`). Production: also put the worker behind
an egress allowlist so only `api.anthropic.com` (and Temporal/Langfuse) is reachable.

**API.** This preview ships no auth. Production: authn/z on `/runs`, per-tenant API keys, request size
limits, and an audit log of who started what.

**Observability.** Beyond traces: per-tenant cost/usage metering from token counts, alerting on
sandbox timeout / non-zero-exit rates, and Temporal worker SLO monitoring.

---

## Project layout

```
src/
  AgentSandbox.Core/                  class library (no entrypoint)
    Settings.cs        env-driven configuration
    Models.cs          records + status objects across the boundaries
    AnthropicClient.cs Messages API over HttpClient (+ retryable/non-retryable errors)
    Tracing.cs         Langfuse ingestion + always-on trace.jsonl mirror
    Sandbox.cs         the swap-point: launches the locked-down container
    Agents.cs          planner / coder-executor / analyst (real Anthropic calls)
    Activities.cs      Temporal activities — the only side effects
    AgentRunWorkflow.cs deterministic orchestration + queryable status
  AgentSandbox.Api/                   ASP.NET Core minimal API (POST /runs, SSE, ...)
    Program.cs
  AgentSandbox.Worker/                Temporal worker host
    Program.cs
tests/AgentSandbox.Tests/             xUnit tests for the pure logic
sandbox/Dockerfile.sandbox            minimal python numpy/pandas image for untrusted code
openai-proxy/                         OpenAI-compatible HTTP proxy that backs Open WebUI (port 8001)
scripts/stream.sh                     helper script for streaming run output
docker-compose.yml                    Temporal (+UI +PG), Langfuse (+PG), api, worker
Dockerfile                            multi-stage .NET build; runtime image has the docker CLI
examples/data.csv                     sample dataset
AgentSandbox.sln · SPEC.md            solution + the groomed spec
```

## Tests

```bash
make test     # dotnet test — pure logic (host-path translation, code extraction, models)
```

## Limitations (preview scope)

No API auth; single-node; no run database beyond Temporal/Langfuse; gVisor requires a Linux host
(auto-falls back to hardened `runc` elsewhere); Firecracker is documented, not wired into compose.
