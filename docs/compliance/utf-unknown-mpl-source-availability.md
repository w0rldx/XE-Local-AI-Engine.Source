# UTF.Unknown (MPL-1.1) — source-availability record

`UTF.Unknown` is a managed charset-detection library shipped as a bundled DLL
inside the application artifacts (used by `PlaintextDocumentReader` for document
ingestion). Its selected license basis is **MPL-1.1**, a weak-copyleft license
whose redistribution obligations are satisfied here as follows.

## Component

| Field | Value |
|-------|-------|
| Package | `UTF.Unknown` |
| Version | `2.6.0` (pinned in `Directory.Packages.props`) |
| Selected license | MPL-1.1 |
| Upstream repository | https://github.com/CharsetDetector/UTF-unknown |
| License text | https://github.com/CharsetDetector/UTF-unknown/blob/master/license/MPL-1.1.txt |
| Manifest override | `scripts/compliance/nuget-license-overrides.json` |

## Tag-to-commit verification

Upstream tag `v2.6` resolves to commit
`7e69ebbdd6ef96a3625fcaf39df42429b8eb0463`, verified via:

```bash
git ls-remote --tags https://github.com/CharsetDetector/UTF-unknown.git v2.6
# 7e69ebbdd6ef96a3625fcaf39df42429b8eb0463  refs/tags/v2.6
```

## Source availability

The library is redistributed **unmodified**. MPL-1.1's source-availability
obligation is met by making the exact corresponding source available at the
immutable upstream commit above:

    https://github.com/CharsetDetector/UTF-unknown/tree/7e69ebbdd6ef96a3625fcaf39df42429b8eb0463

A source archive of that commit is to be retained with the release evidence
bundle for the tagged release. No modifications are made downstream, so no
modified-source disclosure applies. The MPL-1.1 notice is recorded in the
top-level `NOTICE` file.
