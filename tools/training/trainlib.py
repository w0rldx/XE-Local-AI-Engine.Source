"""Pure-python helpers for train.py — no torch, no unsloth, no transformers.

They live here so they can be exercised without a GPU or the multi-gigabyte venv: train.py itself imports unsloth
at module scope (an unsloth requirement), which makes the module unimportable anywhere the runtime is not
installed. Everything in this file is data-shape work — dataset filtering, the parts[] to messages mapping, tool
schema reconstruction and delimiter extraction — which is exactly the part that is worth a test.

Run the checks with ``python3 tools/training/test_trainlib.py``.
"""

import json

CONTRACT_VERSION = 1
CANCELLED_EXIT_CODE = 3


def load_samples(dataset_path, holdout_sequences):
    """Reads the canonical JSONL, dropping the frozen holdout and every sample labelled as behaviour to avoid."""
    with open(dataset_path, "r", encoding="utf-8") as handle:
        return filter_samples((json.loads(line) for line in handle if line.strip()), holdout_sequences)


def filter_samples(records, holdout_sequences):
    holdout = set(holdout_sequences or [])
    kept = []
    for record in records:
        if record.get("sequence") in holdout:
            continue
        # A "Bad" sample demonstrates the behaviour to AVOID. Training on it would teach exactly that.
        if record.get("label") == "Bad":
            continue
        if record.get("reviewState") == "Rejected":
            continue
        kept.append(record)
    return kept


def to_messages(record):
    """Maps one canonical sample's parts[] onto the chat-template message shape."""
    messages = []
    system = record.get("systemInstructions") or ""
    if system:
        messages.append({"role": "system", "content": system})

    for part in sorted(record.get("parts") or [], key=lambda item: item.get("sequence", 0)):
        kind = part.get("kind")
        if kind == "user":
            messages.append({"role": "user", "content": part.get("content") or ""})
        elif kind == "tool":
            # Arguments travel as a dict, not as a JSON string: a template expecting a mapping renders an escaped
            # string as literal text, which is the silent version of this going wrong.
            assistant = {"role": "assistant", "tool_calls": [
                {
                    "type": "function",
                    "function": {"name": part.get("toolName") or "", "arguments": _parse_json(part.get("arguments"))},
                }
            ]}
            if part.get("content"):
                assistant["content"] = part["content"]
            messages.append(assistant)
            result = part.get("result")
            messages.append({"role": "tool", "content": result if isinstance(result, str) else json.dumps(result)})
        else:
            messages.append({"role": "assistant", "content": part.get("content") or ""})

    return messages


def collect_tools(records):
    """Derives a minimal JSON-schema tool list from the calls the dataset actually contains.

    The canonical export carries no tool schemas — only the calls that were made — so the schema is reconstructed
    from the observed argument keys. The template only needs names and parameter names to render its tool block.
    """
    tools = {}
    for record in records:
        for part in record.get("parts") or []:
            if part.get("kind") != "tool":
                continue
            name = part.get("toolName")
            if not name:
                continue
            properties = tools.setdefault(name, {})
            for key in _parse_json(part.get("arguments")):
                properties[key] = {"type": "string"}

    return [
        {
            "type": "function",
            "function": {
                "name": name,
                "description": name.replace("_", " "),
                "parameters": {"type": "object", "properties": properties, "required": list(properties)},
            },
        }
        for name, properties in sorted(tools.items())
    ]


def has_tool_calls(records):
    return any(part.get("kind") == "tool" for record in records for part in record.get("parts") or [])


def delimiter_before(rendered, marker, role_words):
    """The turn delimiter a chat template emitted immediately before ``marker``.

    Hardcoding a per-family guess is how this breaks on the next model: Llama-3, ChatML and Gemma all use different
    markers, and a wrong marker means the loss masking silently trains on the prompts too. Every delimiter in scope
    opens with '<' and names its role right before the content, which is enough to read it back.
    """
    index = rendered.find(marker)
    if index < 0:
        return None
    prefix = rendered[:index]
    role_at = max((prefix.rfind(word) for word in role_words), default=-1)
    if role_at < 0:
        return None
    start = prefix.rfind("<", 0, role_at)
    return prefix[start if start >= 0 else role_at:]


def _parse_json(raw):
    try:
        parsed = json.loads(raw or "{}")
    except json.JSONDecodeError:
        return {}
    return parsed if isinstance(parsed, dict) else {}
