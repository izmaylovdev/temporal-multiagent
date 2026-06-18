#!/usr/bin/env bash
# Stream a run's status as Server-Sent Events.
# Usage: scripts/stream.sh <run_id> [api_base]
set -euo pipefail
RUN_ID="${1:?usage: stream.sh <run_id> [api_base]}"
BASE="${2:-http://localhost:8000}"
curl -N "${BASE}/runs/${RUN_ID}/stream"
