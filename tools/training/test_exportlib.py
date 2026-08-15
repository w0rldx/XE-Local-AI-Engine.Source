"""Self-checks for the pure-python exporter helpers. Run: ``python3 tools/training/test_exportlib.py``.

Deliberately assert-based with no framework, for the same reason as test_trainlib.py: this file has to run inside
the sandboxed training venv, which pins exactly the packages the trainer needs and nothing else.
"""

import json
import os

from exportlib import (
    CONTRACT_VERSION,
    MERGED_DIRECTORY_NAME,
    ExportConfigError,
    merged_directory,
    protocol_line,
    validate_config,
)

JOB = {
    "contractVersion": CONTRACT_VERSION,
    "mode": "merge",
    "basePath": "/data/training/base/checkpoint",
    "adapterDir": "/data/training/runs/r1/staged",
    "outputDir": "/data/training/runs/r1/staged",
}


def test_validate_returns_absolute_paths_and_the_merged_subdirectory():
    base, adapter, merged = validate_config(JOB)

    assert base == "/data/training/base/checkpoint"
    assert adapter == "/data/training/runs/r1/staged"
    assert merged == os.path.join("/data/training/runs/r1/staged", MERGED_DIRECTORY_NAME)


def test_validate_makes_a_relative_path_absolute():
    # A relative path would resolve against the subprocess's own working directory, which is not the run's.
    _, adapter, _ = validate_config(dict(JOB, adapterDir="staged"))

    assert os.path.isabs(adapter)
    assert adapter.endswith(os.path.join(os.getcwd(), "staged"))


def test_validate_rejects_a_foreign_contract_version():
    _expect_error(dict(JOB, contractVersion=CONTRACT_VERSION + 1), "contract version")


def test_validate_rejects_the_adapter_mode():
    # The adapter export runs convert_lora_to_gguf.py on the host and never reaches this script; asking for it here
    # is a configuration mistake, not a supported path.
    _expect_error(dict(JOB, mode="adapter"), "Unsupported export mode")


def test_validate_rejects_a_missing_or_blank_path():
    for key in ("basePath", "adapterDir", "outputDir"):
        _expect_error({item: value for item, value in JOB.items() if item != key}, key)
        _expect_error(dict(JOB, **{key: "   "}), key)


def test_validate_rejects_a_non_object_configuration():
    _expect_error([], "not an object")


def test_merged_directory_is_a_fixed_child_of_the_output_directory():
    assert merged_directory("/x") == os.path.join("/x", MERGED_DIRECTORY_NAME)


def test_protocol_line_is_one_json_object_with_the_event_first():
    line = protocol_line("artifact", kind="MergedHfDir", path="/x/merged-hf")

    assert "\n" not in line
    parsed = json.loads(line)
    assert parsed == {"event": "artifact", "kind": "MergedHfDir", "path": "/x/merged-hf"}
    assert next(iter(parsed)) == "event"


def _expect_error(config, fragment):
    try:
        validate_config(config)
    except ExportConfigError as error:
        assert fragment in str(error), f"expected '{fragment}' in '{error}'"
        return
    raise AssertionError(f"expected an ExportConfigError mentioning '{fragment}'")


def main():
    for name, case in sorted(globals().items()):
        if name.startswith("test_") and callable(case):
            case()
            print(f"ok {name}")
    print(json.dumps({"passed": True}))


if __name__ == "__main__":
    main()
