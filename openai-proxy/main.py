import json
import time
import uuid

import httpx
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, StreamingResponse

API_BASE = "http://api:8000"
MODEL_ID = "agent-sandbox"

app = FastAPI()


@app.get("/v1/models")
async def list_models():
    return {
        "object": "list",
        "data": [{"id": MODEL_ID, "object": "model", "created": int(time.time()), "owned_by": "local"}],
    }


@app.post("/v1/chat/completions")
async def chat_completions(request: Request):
    body = await request.json()
    messages = body.get("messages", [])
    do_stream = body.get("stream", False)

    task = ""
    for msg in reversed(messages):
        if msg.get("role") == "user":
            content = msg.get("content", "")
            task = (
                content
                if isinstance(content, str)
                else " ".join(c.get("text", "") for c in content if c.get("type") == "text")
            )
            break

    if not task:
        return JSONResponse({"error": "no user message"}, status_code=400)

    async with httpx.AsyncClient(timeout=httpx.Timeout(30.0)) as c:
        r = await c.post(f"{API_BASE}/runs", data={"task": task, "tenant": "default"})
        if r.status_code != 202:
            return JSONResponse({"error": f"failed to start run: {r.text}"}, status_code=502)
        run_id = r.json()["run_id"]

    cid = f"chatcmpl-{uuid.uuid4().hex[:12]}"
    ts = int(time.time())

    if do_stream:
        return StreamingResponse(
            _stream(run_id, cid, ts),
            media_type="text/event-stream",
            headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
        )

    content = await _collect(run_id)
    return {
        "id": cid,
        "object": "chat.completion",
        "created": ts,
        "model": MODEL_ID,
        "choices": [{"index": 0, "message": {"role": "assistant", "content": content}, "finish_reason": "stop"}],
        "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0},
    }


def _chunk(cid: str, ts: int, content: str = "", finish: str | None = None) -> str:
    delta = {"content": content} if content else {}
    payload = {
        "id": cid,
        "object": "chat.completion.chunk",
        "created": ts,
        "model": MODEL_ID,
        "choices": [{"index": 0, "delta": delta, "finish_reason": finish}],
    }
    return f"data: {json.dumps(payload)}\n\n"


async def _sse_events(run_id: str):
    """Yield (event_type, status_dict) pairs from the run's SSE stream."""
    async with httpx.AsyncClient(timeout=httpx.Timeout(None)) as c:
        async with c.stream("GET", f"{API_BASE}/runs/{run_id}/stream") as resp:
            ev = None
            async for line in resp.aiter_lines():
                if line.startswith("event:"):
                    ev = line[6:].strip()
                elif line.startswith("data:") and ev:
                    try:
                        yield ev, json.loads(line[5:].strip())
                    except json.JSONDecodeError:
                        pass
                    ev = None


async def _stream(run_id: str, cid: str, ts: int):
    seen: set[tuple] = set()

    async for ev, status in _sse_events(run_id):
        for step in status.get("steps", []):
            key = (step["name"], step.get("iteration", 0), step["status"])
            if key in seen:
                continue
            seen.add(key)
            line = _step_line(step)
            if line:
                yield _chunk(cid, ts, line)

        if ev == "done":
            result = status.get("result") or {}
            state = status.get("state", "failed")
            if state == "succeeded" and result.get("explanation"):
                yield _chunk(cid, ts, "\n\n**Result:**\n" + result["explanation"])
            elif state == "failed":
                yield _chunk(cid, ts, "\n\n**Error:** " + (result.get("error") or "run failed"))
            break

    yield _chunk(cid, ts, finish="stop")
    yield "data: [DONE]\n\n"


async def _collect(run_id: str) -> str:
    async for ev, status in _sse_events(run_id):
        if ev == "done":
            result = status.get("result") or {}
            if status.get("state") == "succeeded":
                return result.get("explanation", "Run completed.")
            return "Error: " + (result.get("error") or "run failed")
    return "Run completed."


def _step_line(step: dict) -> str:
    name = step["name"]
    s = step["status"]
    it = step.get("iteration", 0)
    detail = step.get("detail", "")
    suffix = {"running": " (running...)", "ok": " done", "error": " failed"}.get(s, "")
    if s == "error" and detail:
        suffix += f": {detail[:120]}"
    iter_str = f" [iter {it}]" if it > 0 else ""
    return f"\n[{name}{iter_str}]{suffix}"
