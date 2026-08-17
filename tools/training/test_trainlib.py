"""Self-checks for the pure-python trainer helpers. Run: ``python3 tools/training/test_trainlib.py``.

Deliberately assert-based with no framework: this file has to run inside the sandboxed training venv, which pins
exactly the packages the trainer needs and nothing else.
"""

import json

from trainlib import collect_tools, delimiter_before, filter_samples, has_tool_calls, to_messages

SAMPLE = {
    "schemaVersion": 2,
    "sampleId": "00000000-0000-0000-0000-000000000001",
    "sequence": 0,
    "kind": "tool-call",
    "label": "Good",
    "reviewState": "Approved",
    "systemInstructions": "You call tools.",
    "parts": [
        {"kind": "user", "sequence": 0, "content": "read the readme"},
        {
            "kind": "tool",
            "sequence": 1,
            "toolCallId": "generated-1",
            "toolName": "read_file",
            "arguments": '{"path":"README.md"}',
            "result": "# Title",
            "isError": False,
        },
        {"kind": "text", "sequence": 2, "content": "Here is the readme."},
    ],
}


def test_version_and_stable_id_metadata_are_tolerated_and_preserved():
    kept = filter_samples([SAMPLE], [])

    assert kept[0]["schemaVersion"] == 2
    assert kept[0]["sampleId"] == SAMPLE["sampleId"]
    assert to_messages(kept[0])[1]["content"] == "read the readme"


def test_filter_drops_holdout_bad_and_rejected():
    records = [
        SAMPLE,
        dict(SAMPLE, sequence=1),
        dict(SAMPLE, sequence=2, label="Bad"),
        dict(SAMPLE, sequence=3, reviewState="Rejected"),
    ]

    kept = filter_samples(records, [1])

    assert [record["sequence"] for record in kept] == [0], kept


def test_parts_map_to_messages_with_a_dict_argument():
    messages = to_messages(SAMPLE)

    assert [message["role"] for message in messages] == ["system", "user", "assistant", "tool", "assistant"], messages
    call = messages[2]["tool_calls"][0]
    assert call["function"]["name"] == "read_file"
    # A dict, never a JSON string: a template expecting a mapping renders the string form as literal text.
    assert call["function"]["arguments"] == {"path": "README.md"}, call
    assert messages[3]["content"] == "# Title"
    assert messages[4]["content"] == "Here is the readme."


def test_unparseable_arguments_degrade_to_an_empty_object():
    broken = dict(SAMPLE, parts=[dict(SAMPLE["parts"][1], arguments="{not json")])

    # [0] is the system turn; the tool call is the assistant turn after it.
    call = to_messages(broken)[1]["tool_calls"][0]

    assert call["function"]["arguments"] == {}, call


def test_tools_are_reconstructed_from_observed_arguments():
    tools = collect_tools([SAMPLE])

    assert len(tools) == 1, tools
    function = tools[0]["function"]
    assert function["name"] == "read_file"
    assert function["parameters"]["properties"] == {"path": {"type": "string"}}
    assert function["parameters"]["required"] == ["path"]
    assert has_tool_calls([SAMPLE])
    assert not has_tool_calls([{"parts": [{"kind": "user", "content": "hi"}]}])


def test_delimiters_are_read_back_from_each_family_template():
    chatml = "<|im_start|>system\nS<|im_end|>\n<|im_start|>user\nU<|im_end|>\n<|im_start|>assistant\nA<|im_end|>\n"
    llama = (
        "<|begin_of_text|><|start_header_id|>user<|end_header_id|>\n\nU<|eot_id|>"
        "<|start_header_id|>assistant<|end_header_id|>\n\nA<|eot_id|>"
    )
    gemma = "<bos><start_of_turn>user\nU<end_of_turn>\n<start_of_turn>model\nA<end_of_turn>\n"

    assert delimiter_before(chatml, "U", ("user",)) == "<|im_start|>user\n"
    assert delimiter_before(chatml, "A", ("assistant", "model")) == "<|im_start|>assistant\n"
    assert delimiter_before(llama, "U", ("user",)) == "<|start_header_id|>user<|end_header_id|>\n\n"
    assert delimiter_before(llama, "A", ("assistant", "model")) == "<|start_header_id|>assistant<|end_header_id|>\n\n"
    assert delimiter_before(gemma, "U", ("user",)) == "<start_of_turn>user\n"
    assert delimiter_before(gemma, "A", ("assistant", "model")) == "<start_of_turn>model\n"
    # A template with no recognisable turn marker must report absence rather than guess.
    assert delimiter_before("plain text", "U", ("user",)) is None


def main():
    for name, case in sorted(globals().items()):
        if name.startswith("test_") and callable(case):
            case()
            print(f"ok {name}")
    print(json.dumps({"passed": True}))


if __name__ == "__main__":
    main()
