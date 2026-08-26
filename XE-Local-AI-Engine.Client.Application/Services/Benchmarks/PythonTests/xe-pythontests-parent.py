# The TRUSTED half of the pythonTests harness, fed to the compute sandbox on stdin. It holds the nonce and the
# operator's test code, runs those tests ITSELF, and prints exactly one nonce-marked verdict line to the real stdout.
#
# The candidate never runs in this process. An earlier design ran both in one interpreter behind a "trusted harness"
# namespace and was defeated in a dozen lines: a namespace is not a trust boundary against code in the same address
# space -- walk the ancestor frames for the nonce, write a passing marker to sys.__stdout__, os._exit(0) before the
# real harness prints. The boundary here is a PROCESS boundary, inside the same per-invocation bwrap jail.
#
# Every value C# substitutes below is base64 or hex, so no byte of candidate or operator text ever participates in
# this file's parse. There are no escaping rules to get subtly wrong later.
import base64
import builtins
import ctypes
import io
import json
import os
import select
import subprocess
import sys
import threading
import time
import unittest

_NONCE = "__XE_NONCE__"
_CHILD_B64 = "__XE_CHILD_B64__"
_CANDIDATE_B64 = "__XE_CANDIDATE_B64__"
_TESTS_B64 = "__XE_TESTS_B64__"
_CONFIG_B64 = "__XE_CONFIG_B64__"

_CONFIG = json.loads(base64.b64decode(_CONFIG_B64).decode("utf-8"))
_CALL_TIMEOUT = float(_CONFIG["callTimeoutSeconds"])
_EXPORTS = list(_CONFIG["exports"])
_MAX_CAPTURE_BYTES = 8192

_verdict = {"status": "verdict", "collected": 0, "passed": 0, "failed": 0, "phase": "start", "error": None}


def _emit():
    # sys.__stdout__ rather than sys.stdout: the test phase redirects sys.stdout to stderr so that an operator's
    # print() is evidence rather than noise on the channel C# parses. Nothing else in this process writes to fd 1.
    sys.__stdout__.write("<<<XE-PYTEST:" + _NONCE + ">>>" + json.dumps(_verdict) + "\n")
    sys.__stdout__.flush()


def _unavailable(reason):
    # NOT a zero. A sandbox that cannot be trusted is unscorable; a candidate that misbehaves inside a working sandbox
    # is a zero. C# keeps the two apart, and this is how the parent says which one happened.
    sys.__stdout__.write("<<<XE-PYTEST:" + _NONCE + ">>>"
                         + json.dumps({"status": "unavailable", "reason": reason}) + "\n")
    sys.__stdout__.flush()
    sys.exit(0)


def _harden():
    # PR_SET_DUMPABLE = 4, value 0. A non-dumpable process cannot be ptrace-attached, and /proc/<pid>/mem cannot be
    # opened by a same-uid peer -- which closes the last "read the parent's memory for the nonce, or rewrite the
    # parent's verdict" path. It must happen BEFORE anything is spawned, and a failure refuses rather than degrades.
    try:
        return ctypes.CDLL(None, use_errno=True).prctl(4, 0, 0, 0, 0) == 0
    except BaseException:  # noqa: BLE001 - no libc, no prctl symbol, no ctypes: all mean "cannot harden"
        return False


if not _harden():
    _unavailable("prctl")


class _CandidateError(Exception):
    """What a child-named exception becomes when it is not a builtins Exception subclass."""


class _Boundary:
    """The request/response channel to the untrusted child, with its own read deadline."""

    def __init__(self):
        request_read, self._request_write = os.pipe()
        self._response_read, response_write = os.pipe()
        child_source = (b"_CANDIDATE_B64 = \"" + _CANDIDATE_B64.encode("ascii") + b"\"\n"
                        + base64.b64decode(_CHILD_B64))
        self._process = subprocess.Popen(
            [sys.executable, "-I", "-"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            pass_fds=(request_read, response_write),
            env={
                "XE_REQ_FD": str(request_read),
                "XE_RES_FD": str(response_write),
                "HOME": os.environ.get("HOME", "/tmp"),
                "TMPDIR": os.environ.get("TMPDIR", "/tmp"),
            })
        os.close(request_read)
        os.close(response_write)
        self._process.stdin.write(child_source)
        self._process.stdin.close()
        self._buffer = bytearray()
        self._captured = []
        self._drains = [self._drain(self._process.stdout), self._drain(self._process.stderr)]

    def _drain(self, stream):
        # A reader thread per stream, because the child's pipes are only 64 KiB deep: a chatty candidate that filled
        # one while the parent was blocked on a response would deadlock the whole verification.
        sink = []
        self._captured.append(sink)

        def pump():
            try:
                for chunk in iter(lambda: stream.read(4096), b""):
                    if sum(len(part) for part in sink) < _MAX_CAPTURE_BYTES:
                        sink.append(chunk)
            except (OSError, ValueError):
                pass

        thread = threading.Thread(target=pump, daemon=True)
        thread.start()
        return thread

    def request(self, payload):
        try:
            encoded = json.dumps(payload).encode("utf-8")
        except (TypeError, ValueError) as error:
            raise _CandidateError("the arguments are not JSON-serializable: " + str(error))

        try:
            os.write(self._request_write, encoded + b"\n")
        except OSError:
            raise _CandidateError("the candidate process is no longer accepting calls")

        message = json.loads(self._readline().decode("utf-8", "replace"))
        if "exc" in message:
            self._raise(str(message.get("exc") or ""), str(message.get("msg") or ""))
        if "err" in message:
            raise _CandidateError(str(message.get("err")))

        return message.get("value")

    @staticmethod
    def _raise(name, message):
        # Only builtins, only a type, and only an Exception subclass. SystemExit, KeyboardInterrupt and GeneratorExit
        # are BaseException but NOT Exception, so a child that names one cannot conjure it in the trusted process --
        # it becomes an ordinary test failure instead. sys.modules and eval are deliberately never consulted.
        resolved = getattr(builtins, name, None)
        if isinstance(resolved, type) and issubclass(resolved, Exception):
            raise resolved(message)

        raise _CandidateError(name + ": " + message)

    def _readline(self):
        deadline = time.monotonic() + _CALL_TIMEOUT
        while True:
            newline = self._buffer.find(b"\n")
            if newline >= 0:
                line = bytes(self._buffer[:newline])
                del self._buffer[:newline + 1]
                return line

            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise _CandidateError("the candidate did not answer within " + str(_CALL_TIMEOUT) + "s")

            if not select.select([self._response_read], [], [], remaining)[0]:
                continue

            chunk = os.read(self._response_read, 65536)
            if not chunk:
                raise _CandidateError("the candidate process ended before answering")
            self._buffer.extend(chunk)

    def close(self):
        for closer in (lambda: os.close(self._request_write), lambda: os.close(self._response_read)):
            try:
                closer()
            except OSError:
                pass
        try:
            self._process.kill()
            self._process.wait(timeout=5)
        except BaseException:  # noqa: BLE001 - teardown must not replace a verdict with its own failure
            pass
        for thread in self._drains:
            thread.join(timeout=1)

    def transcript(self):
        return [b"".join(sink)[:_MAX_CAPTURE_BYTES].decode("utf-8", "replace") for sink in self._captured]


class _Candidate:
    """`candidate.solve(10)` -- one attribute access, one request, one answer."""

    def __getattr__(self, name):
        return lambda *args, **kwargs: _BOUNDARY.request({"op": "call", "name": name, "args": list(args), "kwargs": kwargs})


_BOUNDARY = None
_stdout = sys.stdout
try:
    _verdict["phase"] = "spawn"
    _BOUNDARY = _Boundary()

    _candidate = _Candidate()
    _namespace = {
        "__name__": "__xe_tests__",
        "candidate": _candidate,
        "pycall": lambda name, args=(), kwargs=None: _BOUNDARY.request(
            {"op": "call", "name": name, "args": list(args), "kwargs": kwargs or {}}),
        "pyeval": lambda source: _BOUNDARY.request({"op": "eval", "src": source}),
        "CandidateError": _CandidateError,
    }
    for _export in _EXPORTS:
        _namespace[_export] = getattr(_candidate, _export)

    # An operator's print() is evidence, not protocol. fd 1 stays reserved for the single marker line.
    sys.stdout = sys.stderr
    _verdict["phase"] = "tests"
    exec(compile(base64.b64decode(_TESTS_B64), "<tests>", "exec"), _namespace)

    _cases = [value for value in _namespace.values()
              if isinstance(value, type) and issubclass(value, unittest.TestCase)]
    if _cases:
        _verdict["phase"] = "run"
        _suite = unittest.TestSuite(
            [unittest.defaultTestLoader.loadTestsFromTestCase(case) for case in _cases])
        _result = unittest.TextTestRunner(stream=io.StringIO(), verbosity=0).run(_suite)
        _verdict["collected"] = _result.testsRun
        _verdict["failed"] = len(_result.failures) + len(_result.errors)
        if _result.failures or _result.errors:
            _verdict["error"] = (_result.failures + _result.errors)[0][1].strip().splitlines()[-1][:500]
    else:
        # No runner to enumerate, so the whole script is ONE implicit case that passes iff it raised nothing. The
        # alternative -- parsing a framework's textual output -- would couple the scorer to a runner's format.
        _verdict["collected"] = 1
        _verdict["failed"] = 0
    _verdict["passed"] = _verdict["collected"] - _verdict["failed"]
except BaseException as _error:  # noqa: BLE001 - a BaseException escaping the tests must still reach the verdict line
    _verdict["collected"] = max(1, _verdict["collected"])
    _verdict["failed"] = max(1, _verdict["failed"])
    _verdict["passed"] = _verdict["collected"] - _verdict["failed"]
    _verdict["error"] = type(_error).__name__ + ": " + str(_error)[:500]
finally:
    sys.stdout = _stdout
    if _BOUNDARY is not None:
        _child_stdout, _child_stderr = _BOUNDARY.transcript()
        _BOUNDARY.close()
        sys.stderr.write("\n--- candidate stderr ---\n" + _child_stderr + "\n")
        if _child_stdout:
            sys.stderr.write("--- candidate stdout ---\n" + _child_stdout + "\n")
    # There is no path through this process that skips the marker, which is what makes "0 markers implies 0" a
    # statement about the SANDBOX rather than about the tests.
    _emit()
