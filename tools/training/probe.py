"""Training-runtime verification probe.

Prints exactly one line of JSON to stdout and exits 0 whenever the interpreter itself
ran. Import failures are reported inside that JSON (per-package "error" entries plus
``ready: false``) rather than as a traceback, because the C# ``Verifying`` phase reads
this handshake to decide whether a freshly provisioned venv is usable, and a runtime
that is merely missing a package must produce an actionable message rather than a
non-zero exit with a stack trace on stderr.

Exit code 1 is reserved for "could not even emit the handshake".
"""

import json
import platform
import sys
from typing import Any

# Bumped whenever the handshake shape changes. The C# side refuses a runtime whose
# probe reports a different major contract than it was built against.
CONTRACT_VERSION = 1


def _probe_torch(report):
    import torch

    report["torch"] = torch.__version__
    cuda = bool(torch.cuda.is_available())
    report["cudaAvailable"] = cuda
    report["cudaVersion"] = torch.version.cuda
    if cuda:
        report["deviceName"] = torch.cuda.get_device_name(0)
        major, minor = torch.cuda.get_device_capability(0)
        report["deviceCapability"] = f"{major}.{minor}"
        report["deviceCount"] = torch.cuda.device_count()
        report["deviceTotalMemoryBytes"] = torch.cuda.get_device_properties(0).total_memory


def _probe_version(report, key, module_name):
    module = __import__(module_name)
    report[key] = getattr(module, "__version__", "unknown")


def main():
    report: dict[str, Any] = {
        "contractVersion": CONTRACT_VERSION,
        "python": platform.python_version(),
        "platform": sys.platform,
    }
    errors = {}

    # Each import is isolated so one broken package still yields a full report of the rest.
    for key, probe in (
        ("torch", lambda: _probe_torch(report)),
        ("unsloth", lambda: _probe_version(report, "unsloth", "unsloth")),
        ("bitsandbytes", lambda: _probe_version(report, "bitsandbytes", "bitsandbytes")),
        ("transformers", lambda: _probe_version(report, "transformers", "transformers")),
        ("numpy", lambda: _probe_version(report, "numpy", "numpy")),
    ):
        try:
            probe()
        except Exception as exc:  # noqa: BLE001 - any import/runtime failure is a reportable result
            errors[key] = f"{type(exc).__name__}: {exc}"

    if errors:
        report["errors"] = errors
    report["ready"] = not errors and report.get("cudaAvailable", False)

    sys.stdout.write(json.dumps(report, sort_keys=True) + "\n")
    sys.stdout.flush()


if __name__ == "__main__":
    main()
