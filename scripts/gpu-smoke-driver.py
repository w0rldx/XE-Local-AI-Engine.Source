#!/usr/bin/env python3
"""HTTP driver for scripts/run-gpu-smoke-local.sh.

This module does the *talking* and none of the *judging*. Every subcommand performs one
interaction with a live node and prints TAB-separated ``key<TAB>value`` records on stdout;
run-gpu-smoke-local.sh owns every assertion. The split is deliberate:

  * the assertions stay in the shell, where scripts/tests/gpu-smoke.test.sh can drive them
    with synthetic driver output and prove the refuse-to-pass paths really exit non-zero;
  * this file stays a dumb, fakeable data source.

A subcommand exits non-zero only when the *interaction* failed (transport error, auth
failure, a response that does not match the documented contract). "The GPU was not used"
or "no models are installed" are verdicts, not transport failures, and are reported as
records for the shell to judge.

Only stdlib is used: these scripts already hard-depend on python3 (see dev-aspire-common.sh)
and adding a pip dependency to a pre-RC smoke would be a new failure mode of its own.

Values are newline-escaped (``\\n`` -> ``\\\\n``) so one record is always one line.

Chat is driven over SignalR **long polling** rather than WebSockets. LocalChatHub is
``[Authorize(AuthenticationSchemes = JwtBearer)]``; long polling carries the bearer token in
an ordinary Authorization header on every request, needs no websocket client, and is
negotiated by the server (verified live: negotiate advertises WebSockets, ServerSentEvents
and LongPolling). SendMessage returns IAsyncEnumerable<ChatStreamEvent>, so it is invoked as
a SignalR StreamInvocation (message type 4) and the events arrive as StreamItems (type 2).
"""

from __future__ import annotations

import argparse
import contextlib
import json
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

# SignalR's record separator. Every frame on the wire ends with it.
RECORD_SEPARATOR = "\x1e"

# SignalR JSON protocol message types (only the ones this driver needs).
MSG_CLOSE = 7
MSG_COMPLETION = 3
MSG_STREAM_ITEM = 2
MSG_STREAM_INVOCATION = 4

API = "/api/local/v1"

# Loopback hosts whose development certificate is self-signed. TLS verification is disabled
# for these and ONLY these: the Aspire dev cert is not in any trust store, but silently
# accepting an unverified certificate for a non-loopback host would turn a smoke script into
# a way to talk to an impostor. A non-loopback base URL is refused outright.
LOOPBACK_HOSTS = frozenset({"localhost", "127.0.0.1", "::1", "[::1]"})


class DriverError(RuntimeError):
    """An interaction failed: transport, auth, or a response off-contract."""


def emit(key: str, value: object) -> None:
    """Print one ``key<TAB>value`` record."""
    if isinstance(value, bool):
        text = "true" if value else "false"
    elif value is None:
        text = ""
    else:
        text = str(value)
    text = text.replace("\\", "\\\\").replace("\n", "\\n").replace("\r", "").replace("\t", " ")
    print(f"{key}\t{text}", flush=True)


def build_ssl_context(base_url: str) -> ssl.SSLContext | None:
    parts = urllib.parse.urlsplit(base_url)
    if parts.scheme == "http":
        return None
    if parts.scheme != "https":
        raise DriverError(f"unsupported scheme in base URL: {base_url!r}")
    if (parts.hostname or "").lower() not in LOOPBACK_HOSTS:
        raise DriverError(
            f"refusing to disable TLS verification for non-loopback host {parts.hostname!r}. "
            "This smoke only ever targets a locally started development host."
        )
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    return context


class NodeClient:
    def __init__(self, base_url: str, token: str | None = None, timeout: float = 120.0) -> None:
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.timeout = timeout
        self.ssl_context = build_ssl_context(self.base_url)

    def request(
        self,
        method: str,
        path: str,
        body: bytes | str | None = None,
        content_type: str = "application/json",
        expect_binary: bool = False,
        allowed_status: tuple[int, ...] = (),
    ) -> tuple[int, object]:
        url = path if path.startswith("http") else self.base_url + path
        if urllib.parse.urlsplit(url).scheme not in ("http", "https"):
            raise DriverError(f"{method} {path} -> refusing a URL that is not http(s)")
        data = body.encode("utf-8") if isinstance(body, str) else body
        request = urllib.request.Request(url, data=data, method=method)  # noqa: S310  # scheme pinned above
        if self.token:
            request.add_header("Authorization", "Bearer " + self.token)
        if data is not None:
            request.add_header("Content-Type", content_type)
        try:
            # Scheme pinned to http(s) above; file:/custom schemes are unreachable here.
            with urllib.request.urlopen(  # noqa: S310  # nosec B310
                request, context=self.ssl_context, timeout=self.timeout
            ) as response:
                raw = response.read()
                status = response.status
        except urllib.error.HTTPError as error:
            raw = error.read()
            status = error.code
            if status not in allowed_status:
                detail = raw[:400].decode("utf-8", "replace")
                raise DriverError(f"{method} {path} -> HTTP {status}: {detail}") from error
        except urllib.error.URLError as error:
            raise DriverError(f"{method} {path} -> {error.reason}") from error
        if expect_binary:
            return status, raw
        if not raw:
            return status, None
        try:
            return status, json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise DriverError(f"{method} {path} returned a non-JSON body") from error

    def get_json(self, path: str) -> object:
        _, payload = self.request("GET", path)
        return payload


def require_mapping(payload: object, what: str) -> dict:
    if not isinstance(payload, dict):
        raise DriverError(f"{what} did not return a JSON object")
    return payload


# ---------------------------------------------------------------------------
# auth
# ---------------------------------------------------------------------------
def command_auth(args: argparse.Namespace) -> int:
    """Obtain a bearer token, running first-run setup when the node has no operator yet.

    A bare POST auth/login against a fresh node returns 401 and no token, because the node
    reports setupRequired=true until an operator account exists. Chasing that is the whole
    reason this subcommand exists: check auth/status first, POST auth/setup when required
    (409 means another process won the race and an operator already exists), then log in.
    """
    client = NodeClient(args.base_url, timeout=args.timeout)
    status_payload = require_mapping(client.get_json(f"{API}/auth/status"), "auth/status")
    setup_required = bool(status_payload.get("setupRequired"))
    emit("setupRequired", setup_required)

    credentials = json.dumps({"email": args.email, "password": args.password})
    if setup_required:
        code, _ = client.request("POST", f"{API}/auth/setup", credentials, allowed_status=(409,))
        emit("setupPerformed", code != 409)
    else:
        emit("setupPerformed", False)

    _, payload = client.request("POST", f"{API}/auth/login", credentials)
    login = require_mapping(payload, "auth/login")
    token = login.get("accessToken")
    if not isinstance(token, str) or not token:
        raise DriverError(
            "auth/login succeeded but returned no accessToken. The node API is Operator-policy "
            "gated, so the smoke cannot continue without one."
        )
    emit("token", token)
    emit("expiresAtUtc", login.get("expiresAtUtc"))
    return 0


# ---------------------------------------------------------------------------
# runtime identity / device audit
# ---------------------------------------------------------------------------
def command_runtime(args: argparse.Namespace) -> int:
    client = NodeClient(args.base_url, args.token, args.timeout)
    payload = require_mapping(client.get_json(f"{API}/model-fit/llamacpp/runtime"), "llamacpp/runtime")
    installed = payload.get("installed")
    emit("installed", isinstance(installed, dict))
    if isinstance(installed, dict):
        emit("tag", installed.get("tag"))
        # Serialized lowercase by the app: "cpu" | "cuda" | "vulkan".
        emit("variant", installed.get("variant"))
        emit("asset", installed.get("asset"))
        emit("isSourceBuild", bool(installed.get("isSourceBuild")))
    emit("recommendedTag", payload.get("recommendedTag"))
    emit("runningProcessCount", payload.get("runningProcessCount"))
    return 0


def command_audit(args: argparse.Namespace) -> int:
    """Report IRuntimeDeviceAudit's verdict, flattened onto the hardware profile.

    ``refresh=true`` matters: the audit caches only a *determinate* probe, so a stale
    determinate result would otherwise outlive the condition that produced it.
    """
    client = NodeClient(args.base_url, args.token, args.timeout)
    payload = require_mapping(client.get_json(f"{API}/model-fit/hardware-profile?refresh=true"), "hardware-profile")
    for key in (
        "inferenceBackend",
        "gpuExpected",
        "cpuFallback",
        "cpuFallbackReason",
        "cpuFallbackRemediation",
        "gpuVendor",
        "gpuAccelAvailable",
        "vramBytes",
        "vramKnown",
    ):
        if key not in payload:
            raise DriverError(
                f"hardware-profile is missing '{key}'. The device-audit block is the whole "
                "point of this step; a changed contract must fail loudly, not silently pass."
            )
        emit(key, payload[key])
    return 0


# ---------------------------------------------------------------------------
# models / tools / running
# ---------------------------------------------------------------------------
def command_models(args: argparse.Namespace) -> int:
    client = NodeClient(args.base_url, args.token, args.timeout)
    payload = require_mapping(client.get_json(f"{API}/models"), "models")
    items = payload.get("items")
    if not isinstance(items, list):
        raise DriverError("models did not return an items array")
    emit("selectedModelName", payload.get("selectedModelName"))
    emit("count", len(items))
    for item in items:
        if not isinstance(item, dict):
            continue
        name = item.get("modelName")
        if not name:
            continue
        kind = item.get("kind") or item.get("detectedKind") or ""
        # `provider` is REQUIRED downstream, not decoration: this endpoint merges the node-local
        # GGUF models with Ollama and the cloud providers into ONE items array (Ollama first,
        # cloud appended), so without it the shell cannot tell which rows belong to the llama.cpp
        # runtime that steps 1-2 just audited.
        provider = item.get("provider") or ""
        # sizeBytes lets the shell pick the SMALLEST eligible model. On a box with both a 27B and a
        # 0.5B installed that is the difference between a smoke someone runs before every RC and
        # one they skip, and it keeps the run clear of VRAM pressure that would distort step 4.
        size = item.get("sizeBytes")
        size = size if isinstance(size, int) and size >= 0 else 0
        # One record per model: name, kind, tool-capability, provider, size. The shell judges.
        emit("model", f"{name}|{kind}|{str(bool(item.get('isToolCapable'))).lower()}|{provider}|{size}")
    return 0


def command_tools(args: argparse.Namespace) -> int:
    client = NodeClient(args.base_url, args.token, args.timeout)
    payload = require_mapping(client.get_json(f"{API}/tool-catalog"), "tool-catalog")
    tools = payload.get("tools")
    if not isinstance(tools, list):
        raise DriverError("tool-catalog did not return a tools array")
    emit("count", len(tools))
    for tool in tools:
        if isinstance(tool, dict) and tool.get("name"):
            emit("tool", tool["name"])
    return 0


def command_running(args: argparse.Namespace) -> int:
    client = NodeClient(args.base_url, args.token, args.timeout)
    payload = require_mapping(client.get_json(f"{API}/model-fit/running"), "model-fit/running")
    items = payload.get("items")
    if not isinstance(items, list):
        raise DriverError("model-fit/running did not return an items array")
    emit("count", len(items))
    for item in items:
        if isinstance(item, dict) and item.get("modelName"):
            emit("running", f"{item['modelName']}|{item.get('role') or ''}")
    return 0


def command_eject(args: argparse.Namespace) -> int:
    client = NodeClient(args.base_url, args.token, args.timeout)
    body = json.dumps({"modelName": args.model, "role": args.role, "force": args.force})
    _, payload = client.request("POST", f"{API}/model-fit/running/eject", body)
    result = require_mapping(payload, "running/eject")
    emit("modelName", result.get("modelName"))
    emit("outcome", result.get("outcome"))
    return 0


def command_eject_images(args: argparse.Namespace) -> int:
    client = NodeClient(args.base_url, args.token, args.timeout)
    _, payload = client.request(
        "POST", f"{API}/images/runtime/eject", json.dumps({"accepted": True}), allowed_status=(409,)
    )
    result = require_mapping(payload, "images/runtime/eject")
    activity = result.get("activity")
    emit("residentProcessCount", (activity or {}).get("residentProcessCount"))
    emit("reason", result.get("reason"))
    return 0


# ---------------------------------------------------------------------------
# chat over the SignalR hub
# ---------------------------------------------------------------------------
class HubStream:
    """A minimal SignalR long-polling client, scoped to one streamed hub invocation."""

    def __init__(self, client: NodeClient, hub_path: str) -> None:
        self.client = client
        self.hub_path = hub_path
        self.connection_url: str | None = None

    def __enter__(self) -> HubStream:
        _, payload = self.client.request("POST", f"{self.hub_path}/negotiate?negotiateVersion=1", b"")
        negotiate = require_mapping(payload, "hub negotiate")
        token = negotiate.get("connectionToken") or negotiate.get("connectionId")
        if not token:
            raise DriverError("hub negotiate returned neither connectionToken nor connectionId")
        transports = {
            entry.get("transport") for entry in negotiate.get("availableTransports", []) if isinstance(entry, dict)
        }
        if "LongPolling" not in transports:
            raise DriverError(
                f"the chat hub does not advertise LongPolling (offers: {sorted(t for t in transports if t)}). "
                "This driver has no websocket client, so it cannot drive the hub."
            )
        self.connection_url = f"{self.hub_path}?id={urllib.parse.quote(token)}"
        # Protocol handshake. Frames are text/plain: they are not JSON documents but
        # RECORD_SEPARATOR-delimited streams of them.
        self.client.request(
            "POST",
            self.connection_url,
            json.dumps({"protocol": "json", "version": 1}) + RECORD_SEPARATOR,
            "text/plain",
        )
        return self

    def __exit__(self, *_exc: object) -> None:
        if self.connection_url:
            # A best-effort close. The server reaps abandoned long-polling connections on
            # its own, and failing the smoke over the teardown of an already-finished
            # stream would be a false red.
            with contextlib.suppress(DriverError):
                self.client.request("DELETE", self.connection_url)

    def invoke_stream(self, target: str, arguments: list[object], timeout_seconds: float):
        """Send a StreamInvocation and yield each ChatStreamEvent until completion.

        Bounded by WALL CLOCK, not by a poll count. A long poll with nothing to deliver can return
        immediately, so a poll budget is not a proxy for elapsed time: a slow (or CPU-bound) turn
        burns hundreds of empty polls in seconds and would fail for a measurement reason rather
        than a real one. Measured on this box: a CPU-fallback turn exhausted 600 polls while
        generating perfectly well.
        """
        import time

        if self.connection_url is None:
            raise DriverError("stream invoked before the SignalR connection was negotiated")
        frame = (
            json.dumps(
                {
                    "type": MSG_STREAM_INVOCATION,
                    "invocationId": str(uuid.uuid4()),
                    "target": target,
                    "arguments": arguments,
                }
            )
            + RECORD_SEPARATOR
        )
        self.client.request("POST", self.connection_url, frame, "text/plain")

        deadline = time.monotonic() + timeout_seconds
        while time.monotonic() < deadline:
            _, raw = self.client.request("GET", self.connection_url, expect_binary=True)
            if not isinstance(raw, bytes) or not raw:
                continue
            for record in raw.decode("utf-8", "replace").split(RECORD_SEPARATOR):
                if not record.strip():
                    continue
                try:
                    message = json.loads(record)
                except json.JSONDecodeError:
                    continue
                kind = message.get("type")
                if kind == MSG_STREAM_ITEM:
                    item = message.get("item")
                    if isinstance(item, dict):
                        yield item
                elif kind == MSG_COMPLETION:
                    if message.get("error"):
                        raise DriverError(f"hub invocation failed: {message['error']}")
                    return
                elif kind == MSG_CLOSE:
                    raise DriverError(f"hub closed the connection: {message.get('error') or 'no reason given'}")
        raise DriverError(
            f"the chat stream did not complete within {timeout_seconds:.0f}s. "
            "Treating an unfinished turn as a pass is exactly the vacuous green this smoke exists to prevent."
        )


def command_chat(args: argparse.Namespace) -> int:
    client = NodeClient(args.base_url, args.token, args.timeout)
    _, payload = client.request("POST", f"{API}/chat/conversations", json.dumps({"title": args.title}))
    conversation = require_mapping(payload, "chat/conversations")
    conversation_id = conversation.get("conversationId")
    if not conversation_id:
        raise DriverError("chat/conversations returned no conversationId")
    emit("conversationId", conversation_id)

    request_body = {
        "conversationId": conversation_id,
        "content": args.prompt,
        "model": args.model,
        "useLocalTools": args.tools,
    }

    content = ""
    error_text = ""
    event_counts: dict[str, int] = {}
    tools_requested: list[str] = []
    tools_completed: list[str] = []

    with HubStream(client, f"{API}/chat/hub") as stream:
        for event in stream.invoke_stream("SendMessage", [request_body], args.stream_timeout):
            event_type = str(event.get("type") or "")
            event_counts[event_type] = event_counts.get(event_type, 0) + 1
            if event_type == "assistant-completed":
                content = event.get("content") or ""
            elif event_type == "assistant-failed":
                error_text = event.get("error") or "unknown error"
            elif event_type == "tool-call-requested":
                tools_requested.append(str(event.get("toolName") or "?"))
            elif event_type == "tool-call-completed":
                tools_completed.append(str(event.get("toolName") or "?"))

    emit("events", ",".join(f"{name}:{count}" for name, count in sorted(event_counts.items())))
    emit("contentLength", len(content.strip()))
    emit("content", content[:400])
    emit("error", error_text)
    emit("toolsRequested", ",".join(tools_requested))
    emit("toolsCompleted", ",".join(tools_completed))
    return 0


# ---------------------------------------------------------------------------
# image generation
# ---------------------------------------------------------------------------
def command_image_models(args: argparse.Namespace) -> int:
    client = NodeClient(args.base_url, args.token, args.timeout)
    payload = require_mapping(client.get_json(f"{API}/images/models"), "images/models")
    items = payload.get("items")
    if not isinstance(items, list):
        raise DriverError("images/models did not return an items array")
    emit("count", len(items))
    for item in items:
        if isinstance(item, dict) and item.get("modelName"):
            emit("imageModel", item["modelName"])
    return 0


def command_image(args: argparse.Namespace) -> int:
    """Submit one small generation job, poll it, and fetch the bytes back.

    The PNG signature is checked here rather than in the shell only because the response is
    binary; everything else about the verdict (did it succeed, how big) is emitted for the
    shell to judge.
    """
    import time

    client = NodeClient(args.base_url, args.token, args.timeout)
    body = json.dumps(
        {
            "modelName": args.model,
            "prompt": args.prompt,
            "width": args.width,
            "height": args.height,
            "steps": args.steps,
            "seed": str(args.seed),
        }
    )
    _, submitted = client.request("POST", f"{API}/images/jobs", body)
    job = require_mapping(submitted, "images/jobs")
    payload = job
    job_id = job.get("id")
    if not job_id:
        raise DriverError("images/jobs returned no job id")
    emit("jobId", job_id)

    deadline = time.monotonic() + args.wait_seconds
    status = str(job.get("status") or "")
    image_id = job.get("imageId")
    while time.monotonic() < deadline:
        payload = require_mapping(
            client.get_json(f"{API}/images/jobs/{urllib.parse.quote(str(job_id))}"), "images/jobs/{id}"
        )
        status = str(payload.get("status") or "")
        image_id = payload.get("imageId")
        if status.lower() in ("succeeded", "completed", "failed", "cancelled", "canceled"):
            break
        time.sleep(1.0)

    emit("status", status)
    emit("error", payload.get("sanitizedError"))
    emit("durationMs", payload.get("durationMs"))
    emit("width", payload.get("width"))
    emit("height", payload.get("height"))

    if not image_id:
        emit("bytes", 0)
        emit("png", False)
        return 0

    _, raw = client.request("GET", f"{API}/images/{urllib.parse.quote(str(image_id))}", expect_binary=True)
    data = raw if isinstance(raw, bytes) else b""
    emit("bytes", len(data))
    emit("png", data[:8] == b"\x89PNG\r\n\x1a\n")
    return 0


# ---------------------------------------------------------------------------
def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--timeout", type=float, default=120.0)
    sub = parser.add_subparsers(dest="command", required=True)

    auth = sub.add_parser("auth")
    auth.add_argument("--email", required=True)
    auth.add_argument("--password", required=True)
    auth.set_defaults(func=command_auth)

    for name, func in (
        ("runtime", command_runtime),
        ("audit", command_audit),
        ("models", command_models),
        ("tools", command_tools),
        ("running", command_running),
        ("image-models", command_image_models),
        ("eject-images", command_eject_images),
    ):
        node = sub.add_parser(name)
        node.add_argument("--token", required=True)
        node.set_defaults(func=func)

    eject = sub.add_parser("eject")
    eject.add_argument("--token", required=True)
    eject.add_argument("--model", required=True)
    eject.add_argument("--role", default="")
    eject.add_argument("--force", action="store_true")
    eject.set_defaults(func=command_eject)

    chat = sub.add_parser("chat")
    chat.add_argument("--token", required=True)
    chat.add_argument("--model", required=True)
    chat.add_argument("--prompt", required=True)
    chat.add_argument("--title", default="gpu-smoke")
    chat.add_argument("--tools", action="store_true")
    chat.add_argument("--stream-timeout", type=float, default=300.0)
    chat.set_defaults(func=command_chat)

    image = sub.add_parser("image")
    image.add_argument("--token", required=True)
    image.add_argument("--model", required=True)
    image.add_argument("--prompt", default="a small red cube on a white background")
    image.add_argument("--width", type=int, default=256)
    image.add_argument("--height", type=int, default=256)
    image.add_argument("--steps", type=int, default=8)
    image.add_argument("--seed", type=int, default=42)
    image.add_argument("--wait-seconds", type=float, default=300.0)
    image.set_defaults(func=command_image)

    return parser


def main(argv: list[str]) -> int:
    args = build_parser().parse_args(argv)
    try:
        return args.func(args)
    except DriverError as error:
        print(f"[gpu-smoke-driver] {args.command}: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
