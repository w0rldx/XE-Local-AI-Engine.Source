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

Recorded evidence (``http.jsonl`` here, ``http.log`` through lab-api.sh) is deny-by-default for credentials. The rules
fail closed: nothing is returned partly masked.

  * the request and response bodies of every route in ``CREDENTIAL_ROUTES`` (and every SignalR negotiate) are replaced
    by a marker; method, path, status and body size stay. So are the bodies of a request path that is neither a plain
    path nor a full URL to the node itself (``//mcp/…``, ``http:mcp/…``): its route is ambiguous. Re-derive the list
    after adding a route family:
    ``grep -hE 'const string' XE-Local-AI-Engine.Client/Endpoints/Common/LocalApiRoutes*.cs`` and the audit below;
  * a body, a SignalR frame or a string inside either that looks like JSON (``{`` or ``[`` as its first visible
    character, after any whitespace, byte-order mark, zero-width, other format or control character such as NUL) but
    does not parse (a truncated transfer, cut tool arguments) is replaced by a marker as a whole;
  * every query value is masked, in a logged path (absolute or relative, every route) and anywhere in recorded text,
    whatever its name; a query name stays only as a plain identifier after one decoding, otherwise it is masked too.
    A logged path's query runs to its end; one inside other text runs to the next whitespace;
  * every userinfo (``//user:password@``) is masked; scheme, host and path stay;
  * a path segment that is a credential is masked in every logged path, driver error and recorded text: the segment
    after each ``SECRET_SEGMENT_PREFIXES`` prefix, derived from every ``LocalApiRoutes`` template whose parameter name
    ``is_secret_key`` matches (``{token}``; ``…Id``/``…Key`` names such as ``sessionId``, ``keyId``, ``nodeKey`` are
    ids the node lists, not credentials). A logged path whose spelling hides such a segment is masked whole, and the
    bodies of every path holding one are omitted like a credential route's. A URL or path inside recorded text or a
    driver error (a tool result linking a skill preview on a conversation route) whose spelling hides one replaces
    the containing string, body or message by a marker;
  * every other body is scrubbed by ``scrub`` as defence in depth: a field is redacted when its lowercased name is in
    ``SECRET_KEYS`` or ends in one of ``SECRET_KEY_SUFFIXES`` (passwords, tokens, API and private keys, secrets,
    recovery/reset/user/device/one-time/authorization codes, cookies, signatures, credentials, connection strings),
    plus the sibling rules in ``scrub_field``;
  * a credential value (``SECRET_VALUE_PREFIXES``: a PEM block, an inbound MCP, integration or proxy key) anywhere in
    a string, any case, redacts that string, in a JSON name (parsed answers included) redacts that name and its value,
    and anywhere in a body, frame or driver error that is not JSON (a text artifact, an error page) replaces it by a
    marker;
  * a body that is neither JSON nor text (a control character other than tab and line breaks, or bytes that are not
    UTF-8: an image, an archive) is replaced by a marker.

Re-run this audit after adding a request, response or hub DTO; every name it prints must sit on a credential route, be
redacted, or be a non-secret id, count or flag (booleans are never redacted):

  grep -rhoE '[A-Z][A-Za-z0-9]* \\{ get' XE-Local-AI-Engine.Client/Endpoints XE-Local-AI-Engine.Client/Hubs \\
    XE-Local-AI-Engine.Client.Application --include='*.cs' | sort -u \\
    | grep -iE '(pass|secret|token|key|code|cookie|signature|credential|connection|pat|otp|pin) \\{'

The OpenAPI document (XE-Local-AI-Engine.Client.React/openapi/v1.json) lacks the locked pre-host's vault routes, so the
source, not the document, is audited. Dictionary-valued credentials (MCP ``headers``/``env``) and ``isSecret`` header
and environment models have no credential-like name, so their routes must stay in ``CREDENTIAL_ROUTES``.

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
import datetime
import hashlib
import json
import re
import ssl
import struct
import sys
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zlib
from collections.abc import Callable
from pathlib import Path

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


def direct_opener(context: ssl.SSLContext | None = None) -> urllib.request.OpenerDirector:
    """An opener that ignores the proxy variables (``http_proxy``, ``https_proxy``, ``all_proxy``): a loopback node's
    requests, its bearer token and the login password never travel through a proxy."""
    return urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context))


class NodeClient:
    def __init__(self, base_url: str, token: str | None = None, timeout: float = 120.0) -> None:
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.timeout = timeout
        self.ssl_context = build_ssl_context(self.base_url)
        self.opener = direct_opener(self.ssl_context)

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
            raise DriverError(f"{method} {redact_path(path)} -> refusing a URL that is not http(s)")
        data = body.encode("utf-8") if isinstance(body, str) else body
        request = urllib.request.Request(url, data=data, method=method)  # noqa: S310  # scheme pinned above
        if self.token:
            request.add_header("Authorization", "Bearer " + self.token)
        if data is not None:
            request.add_header("Content-Type", content_type)
        try:
            # Scheme pinned to http(s) above; file:/custom schemes are unreachable here.
            with self.opener.open(request, timeout=self.timeout) as response:  # nosec B310
                raw = response.read()
                status = response.status
        except urllib.error.HTTPError as error:
            raw = error.read()
            status = error.code
            if status not in allowed_status:
                # A server can echo the requested URL or a credential in its error body.
                detail = redact(raw.decode("utf-8", "replace"), path, self.base_url)[:400]
                raise DriverError(redact_url(f"{method} {redact_path(path)} -> HTTP {status}: {detail}")) from error
        except urllib.error.URLError as error:
            raise DriverError(redact_url(f"{method} {redact_path(path)} -> {error.reason}")) from error
        if expect_binary:
            return status, raw
        if not raw:
            return status, None
        try:
            return status, json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise DriverError(f"{method} {redact_path(path)} returned a non-JSON body") from error

    def get_json(self, path: str) -> object:
        _, payload = self.request("GET", path)
        return payload


def require_mapping(payload: object, what: str) -> dict:
    if not isinstance(payload, dict):
        raise DriverError(f"{what} did not return a JSON object")
    return payload


def now() -> str:
    return datetime.datetime.now().isoformat(timespec="milliseconds")


def append_jsonl(path: Path, record: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as handle:
        # codeql[py/clear-text-storage-sensitive-data] scrub() redacts credential-like keys at any depth first.
        handle.write(json.dumps(scrub(record), ensure_ascii=False, default=str) + "\n")


# Credential-like JSON names, lowercased (audit: module docstring). A SignalR negotiate answers the connection token
# under connectionToken, and under connectionId before version 1.
SECRET_KEYS = frozenset({"bearer", "connectionid", "pat", "otp"})
# Every wire name ending in "token" but "perToken" is a credential or an opaque handle ("tokens", "tokenIds" and the
# *Tokens counts do not end in it). "code" alone is no suffix: errorCode, reasonCode and a problem's code are ids.
SECRET_KEY_SUFFIXES = (
    "password",
    "passphrase",
    "secret",
    "token",
    "apikey",
    "api_key",
    "privatekey",
    "private_key",
    "recoverycode",
    "resetcode",
    "usercode",
    "devicecode",
    "onetimecode",
    "authorizationcode",
    "cookie",
    "signature",
    "credential",
    "credentials",
    "connectionstring",
)
# Values that are credentials whatever field holds them: PEM blocks and inbound MCP, integration and proxy keys.
SECRET_VALUE_PREFIXES = ("-----BEGIN", "xemcp_", "xeint_", "xeprx_")
# A prefix is searched anywhere and in any case: a key or PEM block in the middle of a text or a line is one too.
SECRET_VALUE = re.compile("|".join(map(re.escape, SECRET_VALUE_PREFIXES)), re.IGNORECASE)


def is_secret_key(key: str) -> bool:
    lowered = key.lower()
    # expertsPerToken and kvBytesPerToken are per-token metrics, not tokens.
    return lowered in SECRET_KEYS or (lowered.endswith(SECRET_KEY_SUFFIXES) and not lowered.endswith("pertoken"))


def scrub_field(key: str, value: object, siblings: dict) -> object:
    """``value`` as recorded under ``key`` of the object whose lowercased names map to ``siblings``, a credential the
    node never returns redacted."""
    if isinstance(value, bool):
        return value  # a flag (isSecret, hasStoredApiKey) is never the credential itself
    name = key.lower()
    # An external app's variable map is never echoed back, and the app manifest, not the request, says which variable
    # is secret: every value is redacted, the names stay.
    if name == "variables" and isinstance(value, dict):
        return scrub(dict.fromkeys(value, "(redacted)"))
    secret = (
        is_secret_key(key)
        # The inbound MCP key create answers its one-time plaintext under a bare "key" beside "apiKey"; a generic
        # "key" anywhere else (a setting name, a map entry) stays readable.
        or (name == "key" and "apikey" in siblings)
        # A cloud or custom-tool header and a custom-tool environment variable carry their value beside an isSecret
        # flag; the node returns the value only when the flag is false.
        or (name == "value" and siblings.get("issecret") is True)
        # A scheduled job's parameter JSON is stored encrypted and never echoed back.
        or (name == "parameters" and isinstance(value, str))
    )
    return "(redacted)" if secret else scrub(value)


def visible_start(text: str) -> str:
    """``text`` from its first visible character. json.loads rejects a leading byte-order mark, zero-width or other
    format character (category Cf) and NUL or another control character (Cc), so whatever follows them is judged."""
    for index, char in enumerate(text):
        if not (char.isspace() or unicodedata.category(char) in ("Cc", "Cf")):
            return text[index:]
    return ""


def scrub(value: object) -> object:
    """Replace credential-like fields (passwords, access/refresh tokens, secrets, API keys) at any depth."""
    if isinstance(value, dict):
        # The node binds JSON names case-insensitively, so names are compared lowercased and recorded as sent. A name
        # repeated in another case counts as secret when any spelling of its flag is true.
        siblings = {str(k).lower(): v for k, v in value.items()}
        siblings.update((str(k).lower(), True) for k, v in value.items() if v is True)
        # A data-defined name (a model-keyed map) can be a credential value itself: the pair becomes one marker pair.
        return dict(
            ("(redacted)", "(redacted)") if SECRET_VALUE.search(str(k)) else (k, scrub_field(str(k), v, siblings))
            for k, v in value.items()
        )
    if isinstance(value, list):
        return [scrub(v) for v in value]
    if not isinstance(value, str):
        return value
    if SECRET_VALUE.search(value):
        return "(redacted)"
    visible = visible_start(value)
    # Tool-call arguments and results, and a recorded SignalR body, arrive as strings holding JSON, so a nested
    # credential would pass through. A record's own omission marker also starts with "[" and stays as it is.
    if (visible[:1] in ("{", "[") or RECORD_SEPARATOR in value) and not OMISSION_MARKER.fullmatch(value):
        return redact(value)
    return redact_url(value)


# A query runs from "?" to whitespace, which a URL never carries unencoded. A "//" authority runs to the first "/", "?",
# "#" or whitespace and its userinfo to the LAST "@" in it (an unencoded email-style username holds one more); the rest
# up to the query is the path and fragment.
QUERY = re.compile(r"\?(\S*)")
AUTHORITY = re.compile(r"//([^\s/?#]*)([^\s?]*)")
# A host is one optional bracketed IP literal and a bracket-free rest (port included).
HOST = re.compile(r"(\[[^][]*\])?[^][]*")
# The literal prefix of every LocalApiRoutes template whose next segment is a parameter is_secret_key names a credential
# (the skill import preview token; an external provider's connectionId, a slug the name rule masks too). The segment
# after a prefix is masked in every logged path and recorded text. Derivation: test_model_matrix_driver.py
# (test_secret_path_segments_are_derived_from_the_route_templates) reads every template and fails on a difference.
SECRET_SEGMENT_PREFIXES = ("external-providers/connections", "skills/import/preview")
SECRET_SEGMENT = re.compile(
    r"(?i)\b("
    + "|".join("/+".join(map(re.escape, prefix.split("/"))) for prefix in SECRET_SEGMENT_PREFIXES)
    + r")/+[^/\s?#]+"
)
# A query name kept in the clear; any other name, once decoded, is masked with its value.
QUERY_NAME = re.compile(r"[A-Za-z_][A-Za-z0-9_.-]*")


def mask_query(query: str) -> str:
    """``query`` (without its ``?``) with every value masked; a name is kept, decoded once, only when it is a plain
    identifier (an encoded ``=`` or ``&`` could hide a value in it)."""
    masked = []
    for part in query.split("&"):
        name, equals, _ = part.partition("=")
        name = urllib.parse.unquote_plus(name)
        masked.append(name + "=(redacted)" if equals and QUERY_NAME.fullmatch(name) else "(redacted)" if part else "")
    return "&".join(masked)


def mask_authority(match: re.Match[str]) -> str:
    """A ``//`` authority with its userinfo masked through the last ``@``. When the split is ambiguous (an ``@`` after
    a path delimiter, where a parser could end the userinfo instead, or unbalanced host brackets), the authority, path
    and fragment become one marker."""
    _, at, host = match[1].rpartition("@")
    if "@" in match[2] or not HOST.fullmatch(host):
        return "//(redacted)"
    return "//" + ("(redacted)@" if at else "") + host + match[2]


def mask_url(text: str) -> str:
    """``text`` with every query value, every userinfo and every secret path segment in its canonical spelling
    masked."""
    text = SECRET_SEGMENT.sub(r"\1/(redacted)", AUTHORITY.sub(mask_authority, text))
    return QUERY.sub(lambda match: "?" + mask_query(match[1]), text)


def redact_url(text: str) -> str:
    """``text`` with every query value, every userinfo and every secret path segment masked, wherever a URL or a
    relative path sits in it: a hub path carries the SignalR connection token as a query value; an MCP server URL can
    carry credentials in either; a skill preview resource link carries its import token. When a word's path (up to its
    first ``?``) still shows a secret segment once ``loose_segments`` reads it (``%73kills/…``, ``skills%2Fimport…``,
    ``skills/./import/…``, ``skills\\import\\…``), no mask can be trusted and the whole text is a marker; so is a text
    holding a credential value anywhere."""
    if SECRET_VALUE.search(text):
        return omitted(text, "credential value")
    for word in text.split():
        head = word.partition("?")[0]
        # A segment masked already (a logged path recorded again) is no secret.
        hidden = secret_segments(head) - {"(redacted)"}
        if hidden & set(loose_segments(SECRET_SEGMENT.sub(r"\1/(redacted)", head))):
            return omitted(text, "unmaskable secret path")
    return mask_url(text)


def loose_segments(path: str) -> list[str]:
    """``path``'s segments as the most lenient router could read them: decoded until stable, lowercased, split at
    slashes and backslashes, empty and dot segments dropped, ``..`` resolved."""
    decoded = path
    while (once := urllib.parse.unquote(decoded)) != decoded:
        decoded = once
    segments: list[str] = []
    for segment in re.split(r"[/\\]+", decoded.lower()):
        if segment == "..":
            segments = segments[:-1]
        elif segment not in ("", "."):
            segments.append(segment)
    return segments


def secret_segments(path: str) -> set[str]:
    """Every segment of ``loose_segments(path)`` that follows a ``SECRET_SEGMENT_PREFIXES`` prefix."""
    segments = loose_segments(path)
    return {
        segments[index + len(prefix)]
        for prefix in (prefix.split("/") for prefix in SECRET_SEGMENT_PREFIXES)
        for index in range(len(segments) - len(prefix))
        if segments[index : index + len(prefix)] == prefix
    }


def redact_path(path: str) -> str:
    """A logged request path or URL: everything after its first ``?`` is one query, whatever whitespace it holds. A
    secret segment in a spelling ``redact_url`` misses (``%73kills/…``, ``skills/./import/…``), or a credential value
    anywhere, masks the whole path."""
    if SECRET_VALUE.search(path):
        return "(redacted)"
    head, mark, query = path.partition("?")
    masked = mask_url(head)
    if secret_segments(head) & set(loose_segments(masked)):
        masked = "(redacted)"
    return masked + (mark + mask_query(query) if mark else "")


# Route families (paths under /api/local/v1, from LocalApiRoutes) whose request or response bodies carry credentials.
# Their bodies are never recorded: a field-name rule cannot see a credential under an arbitrary map key.
CREDENTIAL_ROUTES = (
    "auth",  # login, refresh, setup, change-password, sessions, vault confirm/unlock/unlock-recovery
    "mcp",  # MCP server registrations (url, headers, env) and the inbound MCP server key
    "cloud-settings",  # API key, Entra client secret, secret headers, Entra device-code and auth-code sign-in
    "cloud/codex",  # Codex sign-in
    "external-providers",  # connection API keys and headers
    "external-apps/instances",  # install, update and variable maps
    "custom-tools",  # secret headers and environment variables
    "scheduler/jobs",  # parameter JSON, stored encrypted
    "integrations/keys",  # the plaintext key on create
    "proxy/key",  # the proxy API key
    "model-fit/hf-token",  # the Hugging Face token
)


def is_credential_route(path: str, origin: str | None = None) -> bool:
    """Whether ``path`` (``/api/local/v1/…`` or relative to it, or a full URL to ``origin``, any query) is a credential
    route.

    Only a plain path, or an http(s) URL whose scheme and authority are ``origin``'s, is classified, as the node routes
    it: percent-decoded once, dot segments resolved, empty segments dropped, case ignored. Anything else counts as a
    credential route: a scheme-relative ``//…``, any other scheme or authority (``http:mcp/…``), a control or format
    character (the URL parser drops some), an encoded slash, backslash or percent sign, a ``..`` above the root, a
    segment ``secret_segments`` finds."""
    text = path.strip()
    # A path carrying a credential segment is one too: an answer can echo it in a spelling no mask predicts.
    if any(unicodedata.category(char) in ("Cc", "Cf") for char in text) or secret_segments(text):
        return True
    parts = urllib.parse.urlsplit(text)
    own = urllib.parse.urlsplit(origin or "")
    if (parts.scheme or parts.netloc or text.startswith("//")) and not (
        own.netloc
        and parts.scheme in ("http", "https")
        and (parts.scheme, parts.netloc.lower()) == (own.scheme, own.netloc.lower())
    ):
        return True
    raw = parts.path
    decoded = urllib.parse.unquote(raw)
    if decoded.count("/") != raw.count("/") or "\\" in decoded or "%" in decoded:
        return True
    segments: list[str] = []
    for segment in decoded.lower().split("/"):
        if segment == "..":
            if not segments:
                return True
            segments.pop()
        elif segment not in ("", "."):
            segments.append(segment)
    route = "/".join(segments).removeprefix(API.strip("/") + "/")
    # A SignalR negotiate answers the connection token.
    return route.endswith("/negotiate") or any(route == f or route.startswith(f + "/") for f in CREDENTIAL_ROUTES)


OMISSION_MARKER = re.compile(r"\[body omitted: [a-z -]+, \d+ bytes\]")


def omitted(text: str, reason: str) -> str:
    return f"[body omitted: {reason}, {len(text.encode('utf-8'))} bytes]"


def redact(text: str, path: str | None = None, origin: str | None = None) -> str:
    """Scrub a JSON body's credential-like fields, a SignalR body frame by frame; other text has its URLs masked.
    The body of a credential route (``path``, see ``is_credential_route``), a JSON-shaped body that does not parse, a
    body that is not text and one holding a credential value outside a JSON string are replaced by a marker."""
    if path is not None and text.strip() and is_credential_route(path, origin):
        return omitted(text, "credential-bearing route")
    if RECORD_SEPARATOR in text:
        # The trailing separator makes the whole body invalid JSON, so each frame is parsed on its own.
        return RECORD_SEPARATOR.join(redact(frame) for frame in text.split(RECORD_SEPARATOR))
    visible = visible_start(text)
    try:
        scrubbed = json.dumps(scrub(json.loads(visible)))
    except json.JSONDecodeError:
        # A transfer cut after a complete credential leaves JSON no field rule can parse.
        if visible[:1] in ("{", "["):
            return omitted(text, "unparseable JSON")
        # A decoder's replacement character stands for bytes that were not UTF-8.
        if "\ufffd" in text or any(unicodedata.category(c) == "Cc" and c not in "\t\n\r" for c in text):
            return omitted(text, "not text")
        return redact_url(text)
    # scrub redacts every credential value and name it holds, so a match left means a rule missed one.
    return omitted(text, "credential value") if SECRET_VALUE.search(scrubbed) else scrubbed


class RecordingNodeClient(NodeClient):
    """NodeClient that appends every REST call (hub long polls excepted) to ``http_log()`` at call time, requests and
    responses scrubbed. ``exchange`` is the one unrecorded call; a subclass overrides it to retry."""

    def http_log(self) -> Path:
        raise NotImplementedError

    def exchange(
        self,
        method: str,
        path: str,
        body: bytes | str | None = None,
        content_type: str = "application/json",
        expect_binary: bool = False,
        allowed_status: tuple[int, ...] = (),
    ) -> tuple[int, object]:
        return super().request(method, path, body, content_type, expect_binary, allowed_status)

    def request(
        self,
        method: str,
        path: str,
        body: bytes | str | None = None,
        content_type: str = "application/json",
        expect_binary: bool = False,
        allowed_status: tuple[int, ...] = (),
    ) -> tuple[int, object]:
        poll = "/hub?id=" in path and method in ("GET", "DELETE")
        record: dict = {"t": now(), "method": method, "path": redact_path(path)}
        if body is not None and not poll:
            text = body.decode("utf-8", "replace") if isinstance(body, bytes) else body
            record["request"] = redact(text, path, self.base_url)[:20000]
        started = time.monotonic()
        try:
            status, payload = self.exchange(method, path, body, content_type, expect_binary, allowed_status)
        except DriverError as error:
            message = redact_url(str(error))
            record.update(ms=int((time.monotonic() - started) * 1000), error=message[:4000])
            append_jsonl(self.http_log(), record)
            if message == str(error):
                raise
            # An exchange override can raise its own error; the original stays out of the chain.
            raise DriverError(message) from None
        if not poll:
            # Responses are scrubbed too: auth/login (token renewal) answers with the access token.
            if isinstance(payload, bytes):
                shown: object = redact(payload.decode("utf-8", "replace"), path, self.base_url)[:2000]
            elif payload is not None and is_credential_route(path, self.base_url):
                # The size is the re-serialised JSON's: the transport hands back the parsed body.
                shown = omitted(json.dumps(payload), "credential-bearing route")
            else:
                shown = scrub(payload)
            record.update(ms=int((time.monotonic() - started) * 1000), status=status, response=shown)
            append_jsonl(self.http_log(), record)
        return status, payload


# Download phases that mean "still going".
DOWNLOAD_ACTIVE = ("running", "queued", "pending", "starting", "verifying", "finalizing")


def poll_download(
    get: Callable[[str], dict],
    operation_id: object,
    timeout: float,
    retry: Callable[[DriverError], bool] = lambda _error: False,
) -> str:
    """Poll a model-fit download every 5 s until its phase is terminal or ``timeout`` passes; the last phase seen ("" if
    none). A failed poll raises unless ``retry(error)`` asks for another poll."""
    path = f"/model-fit/gguf/downloads/operations/{urllib.parse.quote(str(operation_id))}"
    deadline, phase = time.monotonic() + timeout, ""
    while time.monotonic() < deadline:
        try:
            phase = str(get(path).get("phase") or "")
        except DriverError as error:
            if not retry(error):
                raise
            continue
        if phase.lower() not in DOWNLOAD_ACTIVE:
            break
        time.sleep(5)
    return phase


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
        # GGUF models with the cloud providers into ONE items array, so without it the shell cannot
        # tell which rows belong to the llama.cpp runtime that steps 1-2 just audited.
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
