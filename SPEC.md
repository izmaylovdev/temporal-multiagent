# agent-sandbox-temporal — Spec

A small service that executes a multi-agent system as a **durable, retryable, resumable Temporal
workflow**. Untrusted, model-generated Python runs inside an **isolated, no-network sandbox**
(gVisor `runsc`, with an automatic fallback to a hardened `runc` container; Firecracker documented
as the production tier).

**Stack:** .NET 8 — ASP.NET Core minimal API + the official `Temporalio` .NET SDK; Anthropic and
Langfuse over HTTP. The sandbox runs the agent-generated **Python** (numpy/pandas), kept
deliberately language-independent from the host service.

## Scenario

The agents solve a **math / data-analysis** task. The pipeline is multi-agent:

```
planner ──▶ coder-executor ──▶ analyst
              │   ▲
              │   │ error-driven self-correction (≤ MAX_ITERATIONS)
              ▼   │
        gVisor sandbox (NO network)
```

- **planner** — decomposes the user's task into a concrete computational plan (what to compute,
  which libraries, which input files).
- **coder-executor** — writes a Python script, runs it in the sandbox, and if it errors / exits
  non-zero, feeds `stdout`/`stderr` back to itself and rewrites. The sandbox result is ground
  truth; there is no LLM "review" step.
- **analyst** — turns the successful program output into a plain-language explanation.

LLM reasoning (real Anthropic calls) happens **host-side only**, as Temporal activities with network
access. The sandbox never has network access and never sees a secret.

## API

`multipart/form-data` so a run can carry optional input files.

| Method | Path                | Purpose                                              |
|--------|---------------------|------------------------------------------------------|
| POST   | `/runs`             | Start a run. Fields: `task` (str), `tenant` (str, optional), `files` (0..N). Returns `{run_id}`. |
| GET    | `/runs/{id}`        | Status snapshot (read from a Temporal workflow query). |
| GET    | `/runs/{id}/stream` | Server-Sent Events stream of status until terminal.  |
| GET    | `/healthz`          | Liveness.                                            |

Uploaded files are stored per-run and mounted **read-only** into the sandbox at `/work/inputs/`.
Guardrails: per-file and per-request size caps, an extension allowlist
(`.csv .tsv .json .txt .parquet .xlsx`), and filename sanitization.

## Workflow

Deterministic workflow; all side effects live in activities. The workflow owns a queryable status
object updated at each step:

1. `plan` activity → plan
2. loop `i` in `0..MAX_ITERATIONS`:
   1. `generate_code` activity (gets the previous error, if any) → script
   2. `execute_in_sandbox` activity → `{exit_code, stdout, stderr, duration, artifacts}`
   3. if `exit_code == 0` → break; else carry the error into the next iteration
3. if no success → terminal `failed`
4. `analyze` activity → explanation
5. terminal `succeeded` with the final result

Durability/resumability come from Temporal: a worker crash mid-run resumes from the last completed
activity. Each iteration is a separate, individually retryable activity and is visible in status.

## Sandbox / isolation model

Per-execution **ephemeral** container:

- `--network none` (no egress, no host network)
- gVisor `runsc` runtime when available (userspace syscall interception) → automatic fallback to
  hardened `runc`
- `--read-only` rootfs, non-root user (`65534:65534`), `--cap-drop ALL`,
  `--security-opt no-new-privileges`, default seccomp
- resource caps: `--memory`, `--cpus`, `--pids-limit`, plus a hard wall-clock timeout (container is
  killed)
- read-only input mount (`/work/inputs`), writable per-run output dir (`/work/output`), small
  `tmpfs /tmp`
- minimal image with `numpy` + `pandas` preinstalled; no package manager, no shell tools needed

**Isolation tiers (strongest → weakest):** Firecracker microVM > gVisor `runsc` > hardened `runc`.
The README documents the Firecracker production path (jailer, one microVM per run, vsock I/O).

## Tracing

Langfuse-style: one **trace per run**, one **span per workflow step** carrying input/output, model,
token usage, latency, and a `tenant` tag. Every span is also mirrored to a per-run `trace.jsonl` and
to stdout, so traces are inspectable even if Langfuse is down or unconfigured. Self-hosted Langfuse
ships in `docker-compose`.

## Retry / timeout strategy

- LLM activities (`plan`, `generate_code`, `analyze`): exponential backoff, bounded attempts;
  non-retryable on auth/4xx.
- `execute_in_sandbox`: limited retries; hard wall-clock timeout enforced **both** inside the run
  and via Temporal `start_to_close_timeout`; activity heartbeats.
- Agent correction loop capped at `MAX_ITERATIONS`.
- Workflow-level execution timeout as a backstop.

## Multi-tenant hardening (documented in README)

Per-tenant task queues / namespaces and resource quotas, ephemeral per-run sandboxes with no shared
writable state, secrets that never cross into the sandbox, default-deny egress, pinned/minimal
sandbox image, input-size and output-truncation limits, rate limiting, and tenant-tagged traces.

## Components / deployment

One `docker-compose.yml`:

- Temporal server + Temporal UI + Postgres
- Langfuse (web + worker) + Postgres + ClickHouse + Redis + MinIO
- the app image, run as two services: **api** (ASP.NET Core minimal API) and **worker** (Temporal worker)

## Out of scope

API auth/login, a persistent run database beyond Temporal/Langfuse, multi-node, production deploy
manifests (compose only).
