# Release authority and provenance register

`release-authority-register.json` is the durable, public-safe publication gate for source and official portable
binaries. It deliberately starts unresolved. A chat statement may be recorded as an input, but it is not a signed
approval and cannot make the gate pass.

For every category, an authorized person must set `status` to `approved` and provide:

- a public-safe approver name or established public alias;
- the approver's authority basis;
- `decision_date` and a future `expires_on` review date;
- at least one non-blank evidence reference; and
- when `repository_path` is used, an existing repository-relative, public-safe evidence file.

Evidence may reference a controlled private record by a non-sensitive record identifier. Do not commit personal
addresses, signatures, employment documents, contracts, certificate secrets, or other private material. A redacted
approval memo or a stable private-record ID is sufficient for the structural gate; legal sufficiency remains an owner
and counsel decision.

Run the gate from the repository root:

```bash
python3 scripts/release/verify-release-authority.py
```

Any missing category, blank required value, unresolved status, expired approval, or missing/escaping evidence path
fails closed. The validator checks structure, completeness, evidence paths, and freshness only. It is not legal
advice and does not certify ownership, authority, or license sufficiency.
