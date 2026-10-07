#!/usr/bin/env python3
"""HTTP driver for scripts/lab-up.sh: brings a lab node's state in line with a profile, one ensure step at a time.

Like scripts/gpu-smoke-driver.py, whose REST and SignalR client it loads by import, this module does the *talking*
and none of the *judging*. Every subcommand prints TAB-separated ``key<TAB>value`` records and exits non-zero only
when an interaction failed (transport, auth, a response off-contract): 1 for that, 2 for a missing prerequisite.
"Not installed" or "not resident" are verdicts, reported as records for lab-up.sh to judge.

Subcommands:
  snapshot-key     sha256 over the sorted EF migration file names; the lab snapshot is valid only for an equal key
  settings-ensure  GET node-settings, PUT only the desired keys that differ, re-GET and verify
  model-ensure     installed?, select as the node default, optionally eject other chat servers, resident?
  download         install a GGUF through the node's own model-fit/download and poll it to a terminal phase
  first-turn       one canned chat turn over the chat hub; wire events and the answer land in the evidence dir
  manifest         write the handoff manifest JSON (pure file I/O)

Every REST call is appended to ``<evidence>/http.jsonl`` when it happens (hub long polls excepted, like the model
matrix driver). Request bodies have password fields redacted. Selecting a model writes the node default only; it does
not load the model, so ``resident false`` after model-ensure is the honest normal case until the first turn.
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import importlib.util
import json
import sys
import time
import urllib.parse
from pathlib import Path

_SPEC = importlib.util.spec_from_file_location("gpu_smoke_driver", Path(__file__).with_name("gpu-smoke-driver.py"))
if _SPEC is None or _SPEC.loader is None:
    raise ImportError("scripts/gpu-smoke-driver.py")
gsd = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(gsd)

API = gsd.API
emit = gsd.emit
# Download phases that mean "still going"; copied from model-matrix-driver.py's command_download.
DOWNLOAD_ACTIVE = ("running", "queued", "pending", "starting", "verifying", "finalizing")


class PrerequisiteError(RuntimeError):
    """Something the step needs is missing; nothing was talked to or judged."""


def now() -> str:
    return datetime.datetime.now().isoformat(timespec="milliseconds")


def append_jsonl(path: Path, record: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(record, ensure_ascii=False, default=str) + "\n")


SECRET_KEYS = frozenset({"accesstoken", "refreshtoken", "token", "bearer", "password", "secret", "apikey", "api_key"})


def is_secret_key(key: str) -> bool:
    lowered = key.lower()
    return lowered in SECRET_KEYS or lowered.endswith(("password", "secret", "apikey"))


def scrub(value: object) -> object:
    """Replace credential-like fields (passwords, access/refresh tokens, secrets, API keys) at any depth."""
    if isinstance(value, dict):
        return {k: "(redacted)" if is_secret_key(str(k)) else scrub(v) for k, v in value.items()}
    if isinstance(value, list):
        return [scrub(v) for v in value]
    return value


def redact(text: str) -> str:
    """Scrub a JSON body's credential-like fields; a non-JSON body is returned unchanged."""
    try:
        return json.dumps(scrub(json.loads(text)))
    except json.JSONDecodeError:
        return text


class RecordingClient(gsd.NodeClient):
    """NodeClient that appends every REST call to ``<evidence>/http.jsonl`` at call time. No retries, no relogin."""

    def __init__(self, base_url: str, token: str, evidence: Path) -> None:
        super().__init__(base_url, token)
        self.log = evidence / "http.jsonl"

    def request(self, method, path, body=None, content_type="application/json", expect_binary=False, allowed_status=()):
        poll = "/hub?id=" in path and method in ("GET", "DELETE")
        record: dict = {"t": now(), "method": method, "path": path}
        if body is not None and not poll:
            record["request"] = redact(body.decode("utf-8", "replace") if isinstance(body, bytes) else body)[:20000]
        started = time.monotonic()
        try:
            status, payload = super().request(method, path, body, content_type, expect_binary, allowed_status)
        except gsd.DriverError as error:
            record.update(ms=int((time.monotonic() - started) * 1000), error=str(error)[:4000])
            append_jsonl(self.log, record)
            raise
        if not poll:
            # Responses are scrubbed too: auth/login (token renewal) answers with the access token.
            shown = payload.decode("utf-8", "replace")[:2000] if isinstance(payload, bytes) else scrub(payload)
            record.update(ms=int((time.monotonic() - started) * 1000), status=status, response=shown)
            append_jsonl(self.log, record)
        return status, payload

    def get(self, path: str) -> dict:
        return gsd.require_mapping(self.request("GET", API + path)[1], path)

    def send(self, method: str, path: str, body: object) -> dict:
        return gsd.require_mapping(self.request(method, API + path, json.dumps(body))[1], path)


def client_for(args: argparse.Namespace) -> RecordingClient:
    return RecordingClient(args.base_url, args.token, Path(args.evidence))


def same(a: object, b: object) -> bool:
    """JSON equality: Python's ``1 == True`` must not make a bool setting equal to an int one."""
    return json.dumps(a, sort_keys=True) == json.dumps(b, sort_keys=True)


def running_items(client: RecordingClient) -> list[dict]:
    items = client.get("/model-fit/running").get("items")
    if not isinstance(items, list):
        raise gsd.DriverError("model-fit/running did not return an items array")
    return [i for i in items if isinstance(i, dict) and i.get("modelName")]


# Subcommands


def command_snapshot_key(args: argparse.Namespace) -> int:
    root = Path(args.migrations_dir)
    names = sorted(
        p.name
        for p in root.glob("*.cs")
        if not p.name.endswith(".Designer.cs") and not p.name.endswith("ModelSnapshot.cs")
    )
    if not names:
        raise PrerequisiteError(f"no migration files under {root}")
    emit("snapshotKey", hashlib.sha256("\n".join(names).encode("utf-8")).hexdigest())
    emit("migrationCount", len(names))
    return 0


def load_desired(text: str) -> dict:
    try:
        raw = Path(text[1:]).read_text(encoding="utf-8") if text.startswith("@") else text
        desired = json.loads(raw)
    except (OSError, json.JSONDecodeError) as error:
        raise PrerequisiteError(f"--desired is not readable JSON: {error}") from error
    if not isinstance(desired, dict):
        raise PrerequisiteError("--desired must be a JSON object")
    return desired


def command_settings_ensure(args: argparse.Namespace) -> int:
    """The PUT is a partial merge (SaveNodeSettingsEndpoint), so only the differing keys are sent."""
    desired = load_desired(args.desired)
    client = client_for(args)
    current = client.get("/node-settings")
    changed = {k: v for k, v in desired.items() if not same(current.get(k), v)}
    for key in changed:
        emit("changed", key)
    emit("skipped", not changed)
    if changed:
        client.send("PUT", "/node-settings", changed)
        current = client.get("/node-settings")
    emit("verified", all(same(current.get(k), v) for k, v in desired.items()))
    return 0


def command_model_ensure(args: argparse.Namespace) -> int:
    client = client_for(args)
    listing = client.get("/models")
    items = listing.get("items")
    if not isinstance(items, list):
        raise gsd.DriverError("models did not return an items array")
    installed = any(isinstance(i, dict) and i.get("modelName") == args.model for i in items)
    emit("installed", installed)
    if not installed:
        # An empty listing with isAvailable false means the node sees NO GGUF at all (e.g. a models dir of symlinks,
        # which the sidecar validation rejects), which is a different problem from one missing model.
        if listing.get("isAvailable") is False:
            emit("providerError", str(listing.get("error") or "no GGUF installed"))
        return 0
    previous = str(client.get("/node-settings").get("defaultModelName") or "")
    emit("previouslySelected", previous)
    if previous != args.model:
        previous = client.send("POST", "/models/select", {"modelName": args.model}).get("selectedModelName")
    emit("selected", previous)
    if args.eject_others:
        # Only chat servers compete with the selected model; embedding/reranker servers back retrieval and stay.
        for item in running_items(client):
            role = str(item.get("role") or "")
            if role != "chat":
                emit("kept", f"{item['modelName']}|{role}")
            elif item["modelName"] != args.model:
                body = {"modelName": item["modelName"], "role": role, "force": False}
                # HTTP 200 does not mean gone: `timed_out_still_busy` leaves the server resident while a turn runs.
                outcome = str(client.send("POST", "/model-fit/running/eject", body).get("outcome") or "")
                if outcome in ("ejected", "forced"):
                    emit("ejected", item["modelName"])
                elif outcome == "not_running":
                    # Gone between the listing and the eject (idle reaper): absent is what was wanted, nothing changed.
                    emit("gone", item["modelName"])
                else:
                    emit("ejectFailed", f"{item['modelName']}|{outcome or 'unknown'}")
    emit("resident", any(i["modelName"] == args.model for i in running_items(client)))
    return 0


def renew_token(client: RecordingClient, email: str, password: str) -> str:
    """Log in again (a token lives ~15 min, a download can take an hour) and arm the client with the new token."""
    client.token = None
    token = client.send("POST", "/auth/login", {"email": email, "password": password}).get("accessToken")
    if not isinstance(token, str) or not token:
        raise gsd.DriverError("auth/login returned no accessToken while renewing the token")
    client.token = token
    return token


def command_download(args: argparse.Namespace) -> int:
    """Polling copied from model-matrix-driver.py's command_download; a non-completed phase is a verdict here.

    With --email/--password the poll re-logs-in on HTTP 401 (bounded), so a download longer than the access token's
    lifetime is not reported as a failure; the renewed token is emitted for the caller to keep.
    """
    client = client_for(args)
    renewals, renewed = 0, ""
    started = client.send(
        "POST", "/model-fit/download", {"repoId": args.repo, "fileName": args.file, "includeProjector": False}
    )
    if not started.get("operationId"):
        raise gsd.DriverError("model-fit/download returned no operationId")
    operation = urllib.parse.quote(str(started["operationId"]))
    deadline, phase = time.monotonic() + args.timeout, ""
    while time.monotonic() < deadline:
        try:
            phase = str(client.get(f"/model-fit/gguf/downloads/operations/{operation}").get("phase") or "")
        except gsd.DriverError as error:
            if "HTTP 401" not in str(error) or not args.email or renewals >= 5:
                raise
            renewals += 1
            renewed = renew_token(client, args.email, args.password)
            continue
        if phase.lower() not in DOWNLOAD_ACTIVE:
            break
        time.sleep(5)
    emit("model", args.model)
    if renewed:
        emit("token", renewed)
        emit("tokenRenewals", renewals)
    emit("downloaded", phase.lower() == "completed")
    emit("phase", phase)
    return 0


def command_first_turn(args: argparse.Namespace) -> int:
    client = client_for(args)
    out = Path(args.evidence) / "first-turn"
    out.mkdir(parents=True, exist_ok=True)
    conversation_id = client.send("POST", "/chat/conversations", {"title": "lab-first-turn"}).get("conversationId")
    if not conversation_id:
        raise gsd.DriverError("chat/conversations returned no conversationId")
    emit("conversationId", conversation_id)
    body = {"conversationId": conversation_id, "content": args.prompt, "model": args.model, "useLocalTools": args.tools}
    answer, failed, tool_calls, tool_completed = "", None, 0, 0
    with gsd.HubStream(client, f"{API}/chat/hub") as stream:
        for event in stream.invoke_stream("SendMessage", [body], args.stream_timeout):
            append_jsonl(out / "events.jsonl", {"t": now(), **event})
            kind = event.get("type")
            if kind == "assistant-completed":
                answer = str(event.get("content") or "")
            elif kind == "assistant-failed":
                failed = str(event.get("error") or "unknown error")
            elif kind == "tool-call-requested":
                tool_calls += 1
            elif kind == "tool-call-completed":
                tool_completed += 1
    (out / "answer.txt").write_text(answer, encoding="utf-8")
    emit("answerChars", len(answer.strip()))
    emit("toolCalls", tool_calls)
    emit("toolCallsCompleted", tool_completed)
    if failed is not None:
        emit("failed", failed[:400])
    emit("resident", any(i["modelName"] == args.model for i in running_items(client)))
    return 0


def parse_value(text: str) -> object:
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return text


def command_manifest(args: argparse.Namespace) -> int:
    manifest: dict = {}
    for item in args.set:
        key, sep, value = item.partition("=")
        if not sep or not key:
            raise PrerequisiteError(f"--set expects key=value, got {item!r}")
        manifest[key] = parse_value(value)
    phases: dict = {}
    for item in args.phase:
        name, sep, rest = item.partition("=")
        status, _, reason = rest.partition(":")
        if not sep or not name or not status:
            raise PrerequisiteError(f"--phase expects name=status:reason, got {item!r}")
        phases[name] = {"status": status, "reason": reason}
    manifest["phases"] = phases
    text = json.dumps(manifest, indent=2, ensure_ascii=False)
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(text + "\n", encoding="utf-8")
    print(text)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    key = sub.add_parser("snapshot-key")
    key.add_argument("--migrations-dir", required=True)
    key.set_defaults(func=command_snapshot_key)

    manifest = sub.add_parser("manifest")
    manifest.add_argument("--out", required=True)
    manifest.add_argument("--set", action="append", default=[], metavar="KEY=VALUE")
    manifest.add_argument("--phase", action="append", default=[], metavar="NAME=STATUS:REASON")
    manifest.set_defaults(func=command_manifest)

    def node(name: str, func) -> argparse.ArgumentParser:
        parser_ = sub.add_parser(name)
        parser_.add_argument("--base-url", required=True)
        parser_.add_argument("--token", required=True)
        parser_.add_argument("--evidence", required=True)
        parser_.set_defaults(func=func)
        return parser_

    node("settings-ensure", command_settings_ensure).add_argument("--desired", required=True)
    ensure = node("model-ensure", command_model_ensure)
    ensure.add_argument("--model", required=True)
    ensure.add_argument("--eject-others", action="store_true")
    download = node("download", command_download)
    download.add_argument("--model", required=True)
    download.add_argument("--repo", required=True)
    download.add_argument("--file", required=True)
    download.add_argument("--timeout", type=float, default=3600.0)
    download.add_argument("--email", default="")
    download.add_argument("--password", default="")
    turn = node("first-turn", command_first_turn)
    turn.add_argument("--model", required=True)
    turn.add_argument("--prompt", required=True)
    turn.add_argument("--tools", action="store_true")
    turn.add_argument("--stream-timeout", type=float, default=600.0)
    return parser


def main(argv: list[str]) -> int:
    args = build_parser().parse_args(argv)
    try:
        return args.func(args)
    except PrerequisiteError as error:
        print(f"[lab-driver] PREREQUISITE: {error}", file=sys.stderr)
        return 2
    except gsd.DriverError as error:
        print(f"[lab-driver] {args.command}: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
