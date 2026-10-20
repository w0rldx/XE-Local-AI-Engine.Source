# UTF.Unknown (MPL-1.1) — source-availability record

`UTF.Unknown` is a managed charset-detection library shipped as a bundled DLL
inside the application artifacts (used by `PlaintextDocumentReader` for document
ingestion). Its selected license basis is **MPL-1.1**, a weak-copyleft license
whose redistribution obligations are satisfied here as follows.

## Component

| Field | Value |
|-------|-------|
| Package | `UTF.Unknown` |
| Version | `2.8.0` (pinned in `Directory.Packages.props`) |
| Selected license | MPL-1.1 |
| Upstream repository | https://github.com/CharsetDetector/UTF-unknown |
| License text | https://github.com/CharsetDetector/UTF-unknown/blob/master/license/MPL-1.1.txt |
| Manifest override | `scripts/compliance/nuget-license-overrides.json` |

## Tag-to-commit verification

Upstream tag `v2.8` resolves to commit
`4654fbf0cdb47705e74a2c74bae20d486eb23fa8`, verified via:

```bash
git ls-remote --tags https://github.com/CharsetDetector/UTF-unknown.git v2.8
# 4654fbf0cdb47705e74a2c74bae20d486eb23fa8  refs/tags/v2.8
```

## Source availability

The library is redistributed **unmodified**. MPL-1.1's source-availability
obligation is met by making the exact corresponding source available at the
immutable upstream commit above:

    https://github.com/CharsetDetector/UTF-unknown/tree/4654fbf0cdb47705e74a2c74bae20d486eb23fa8

A source archive of that commit is to be retained with the release evidence
bundle for the tagged release. No modifications are made downstream, so no
modified-source disclosure applies. The MPL-1.1 notice is recorded in the
top-level `NOTICE` file.
