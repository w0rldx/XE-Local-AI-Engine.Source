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
import hashlib
import json
import ssl
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zlib

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


AUDIT_KEYS = (
    "inferenceBackend",
    "gpuExpected",
    "cpuFallback",
    "cpuFallbackReason",
    "cpuFallbackRemediation",
    "gpuVendor",
    "gpuAccelAvailable",
    "vramBytes",
    "vramKnown",
)


def device_audit(client: NodeClient) -> dict:
    """IRuntimeDeviceAudit's verdict, flattened onto the hardware profile; also used by model-matrix-driver.py.

    ``refresh=true`` matters: the audit caches only a *determinate* probe, so a stale
    determinate result would otherwise outlive the condition that produced it.
    """
    payload = require_mapping(client.get_json(f"{API}/model-fit/hardware-profile?refresh=true"), "hardware-profile")
    for key in AUDIT_KEYS:
        if key not in payload:
            raise DriverError(
                f"hardware-profile is missing '{key}'. The device-audit block is the whole "
                "point of this step; a changed contract must fail loudly, not silently pass."
            )
    return payload


def command_audit(args: argparse.Namespace) -> int:
    """Report the device audit as records."""
    payload = device_audit(NodeClient(args.base_url, args.token, args.timeout))
    for key in AUDIT_KEYS:
        emit(key, payload[key])
    return 0


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
        than a real one. Measured in one local run: a CPU-fallback turn exhausted 600 polls while
        generating perfectly well.
        """
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
            # Step 7 picks its negative control from this: a model without `reference` must
            # refuse it, a model with it must run it for real.
            modes = item.get("editModes")
            modes = modes if isinstance(modes, list) else []
            emit("imageModelEditModes", f"{item['modelName']}|{','.join(str(m) for m in modes)}")
    return 0


TERMINAL_STATUSES = ("succeeded", "completed", "failed", "cancelled", "canceled")


def run_image_job(client: NodeClient, body: dict, wait_seconds: float) -> dict:
    """Submit one job, poll it to a terminal state, fetch the bytes back, emit the shared records.

    Returns the last job payload plus ``submitMs`` (wall time of the POST alone). The PNG
    signature is checked here rather than in the shell only because the response is binary;
    everything else about the verdict is emitted for the shell to judge.
    """
    started = time.monotonic()
    _, submitted = client.request("POST", f"{API}/images/jobs", json.dumps(body))
    submit_ms = int((time.monotonic() - started) * 1000)
    job = require_mapping(submitted, "images/jobs")
    payload = job
    job_id = job.get("id")
    if not job_id:
        raise DriverError("images/jobs returned no job id")
    emit("jobId", job_id)

    deadline = time.monotonic() + wait_seconds
    status = str(job.get("status") or "")
    image_id = job.get("imageId")
    while time.monotonic() < deadline:
        payload = require_mapping(
            client.get_json(f"{API}/images/jobs/{urllib.parse.quote(str(job_id))}"), "images/jobs/{id}"
        )
        status = str(payload.get("status") or "")
        image_id = payload.get("imageId")
        if status.lower() in TERMINAL_STATUSES:
            break
        time.sleep(1.0)

    emit("status", status)
    emit("error", payload.get("sanitizedError"))
    emit("durationMs", payload.get("durationMs"))
    emit("width", payload.get("width"))
    emit("height", payload.get("height"))

    data = b""
    if image_id:
        _, raw = client.request("GET", f"{API}/images/{urllib.parse.quote(str(image_id))}", expect_binary=True)
        data = raw if isinstance(raw, bytes) else b""
    emit("bytes", len(data))
    emit("png", data[:8] == PNG_SIGNATURE)
    emit("sha256", hashlib.sha256(data).hexdigest() if data else None)
    return {**payload, "submitMs": submit_ms}


def command_image(args: argparse.Namespace) -> int:
    """Submit one small text-to-image job, poll it, and fetch the bytes back."""
    client = NodeClient(args.base_url, args.token, args.timeout)
    body = {
        "modelName": args.model,
        "prompt": args.prompt,
        "width": args.width,
        "height": args.height,
        "steps": args.steps,
        "seed": str(args.seed),
    }
    run_image_job(client, body, args.wait_seconds)
    return 0


PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def png_bytes(width: int, height: int, variant: int = 1) -> bytes:
    """A two-colour gradient RGB PNG (red left-to-right, blue top-to-bottom), stdlib only.

    img2img needs structure to transform; a flat colour would make "the edit did something"
    indistinguishable from noise when a human looks at the result. Variant 2 inverts every
    channel so the smoke has a second, clearly different source for its two-source check.
    """

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    rows = bytearray()
    for y in range(height):
        rows.append(0)  # filter: none
        blue = y * 255 // max(height - 1, 1)
        for x in range(width):
            pixel = (x * 255 // max(width - 1, 1), 64, blue)
            rows += bytes(255 - c for c in pixel) if variant == 2 else bytes(pixel)
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)  # 8-bit RGB
    return PNG_SIGNATURE + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b"")


def encode_multipart(field: str, filename: str, content_type: str, data: bytes) -> tuple[bytes, str]:
    """One-file multipart/form-data body; returns (body, Content-Type header value)."""
    boundary = "xe-smoke-" + uuid.uuid4().hex
    head = (
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="{field}"; filename="{filename}"\r\n'
        f"Content-Type: {content_type}\r\n\r\n"
    ).encode()
    tail = f"\r\n--{boundary}--\r\n".encode()
    return head + data + tail, f"multipart/form-data; boundary={boundary}"


def command_image_fixture(args: argparse.Namespace) -> int:
    with open(args.out, "wb") as handle:
        handle.write(png_bytes(args.size, args.size, args.variant))
    emit("path", args.out)
    emit("width", args.size)
    emit("height", args.size)
    return 0


def command_image_upload(args: argparse.Namespace) -> int:
    client = NodeClient(args.base_url, args.token, args.timeout)
    with open(args.file, "rb") as handle:
        data = handle.read()
    content_type = "image/png" if data[:8] == PNG_SIGNATURE else "image/jpeg"
    body, header = encode_multipart("file", args.file.rsplit("/", 1)[-1], content_type, data)
    started = time.monotonic()
    _, payload = client.request("POST", f"{API}/images/uploads", body, header)
    upload = require_mapping(payload, "images/uploads")
    if not upload.get("imageId"):
        raise DriverError("images/uploads returned no imageId")
    emit("uploadedImageId", upload["imageId"])
    emit("width", upload.get("width"))
    emit("height", upload.get("height"))
    emit("uploadMs", int((time.monotonic() - started) * 1000))
    return 0


def edit_body(args: argparse.Namespace) -> dict:
    body: dict = {
        "modelName": args.model,
        "prompt": args.prompt,
        "width": args.width,
        "height": args.height,
        "steps": args.steps,
        "seed": str(args.seed),
        "editMode": args.mode,
        "sourceImageId": args.source,
    }
    if args.strength is not None:
        body["strength"] = args.strength
    return body


def command_image_edit(args: argparse.Namespace) -> int:
    client = NodeClient(args.base_url, args.token, args.timeout)
    result = run_image_job(client, edit_body(args), args.wait_seconds)
    emit("editMode", result.get("editMode"))
    emit("sourceImageId", result.get("sourceImageId"))
    emit("submitMs", result["submitMs"])
    return 0


def command_image_edit_negative(args: argparse.Namespace) -> int:
    """Submit an edit the node must refuse. Emits the verdict; the shell judges it.

    ``refused`` = any 4xx (the body is emitted as ``message`` so the shell can check the fixed
    text), ``accepted`` = the node took the job, ``error`` = anything else (5xx, transport).
    """
    client = NodeClient(args.base_url, args.token, args.timeout)
    try:
        status, payload = client.request(
            "POST", f"{API}/images/jobs", json.dumps(edit_body(args)), allowed_status=tuple(range(400, 500))
        )
    except DriverError as error:
        emit("negative", "error")
        emit("httpStatus", "")
        emit("message", str(error)[:400])
        return 0
    emit("negative", "refused" if 400 <= status < 500 else "accepted")
    emit("httpStatus", status)
    emit("message", json.dumps(payload)[:400] if payload is not None else "")
    return 0


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

    fixture = sub.add_parser("image-fixture")
    fixture.add_argument("--out", required=True)
    fixture.add_argument("--size", type=int, default=256)
    fixture.add_argument("--variant", type=int, choices=(1, 2), default=1)
    fixture.set_defaults(func=command_image_fixture)

    upload = sub.add_parser("image-upload")
    upload.add_argument("--token", required=True)
    upload.add_argument("--file", required=True)
    upload.set_defaults(func=command_image_upload)

    for name, func in (("image-edit", command_image_edit), ("image-edit-negative", command_image_edit_negative)):
        edit = sub.add_parser(name)
        edit.add_argument("--token", required=True)
        edit.add_argument("--model", required=True)
        edit.add_argument("--source", required=True)
        # Free text on purpose: the negative control sends `bogus` and expects validation to refuse it.
        edit.add_argument("--mode", required=True)
        edit.add_argument("--strength", type=float, default=None)
        edit.add_argument("--prompt", default="the same scene as a watercolour painting")
        edit.add_argument("--width", type=int, default=256)
        edit.add_argument("--height", type=int, default=256)
        edit.add_argument("--steps", type=int, default=8)
        edit.add_argument("--seed", type=int, default=42)
        edit.add_argument("--wait-seconds", type=float, default=300.0)
        edit.set_defaults(func=func)

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
