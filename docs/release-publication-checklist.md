# Official portable release publication checklist

This checklist applies to the current portable artifacts published through Velopack. It does not assert that an
installer, signing certificate, or signed stable artifact exists.

## Authority gate

- [ ] Replace every `unresolved` decision in `docs/compliance/release-authority-register.json` with durable,
      named, dated, evidenced approval.
- [ ] Keep author identity entries public-safe: use the two established public identities or aliases; do not publish
      new private identity data.
- [ ] Document permissions or non-applicability for employer, contractor, predecessor-project, and C0re material.
- [ ] Account for copied/adapted schemas, migrations, golden vectors, templates, logos/media/branding, and vendored
      agency-agents.
- [ ] Bind the third-party/native/WASM/.NET/Velopack redistribution decision to the release's license inventory and
      artifact evidence.
- [ ] Name who is authorized to create the canonical tag and publish the corresponding portable binaries.
- [ ] Record the unsigned-build risk decision. A future certificate does not retroactively validate an earlier
      artifact; update the decision and evidence when signing is introduced.
- [ ] Run `python3 scripts/release/verify-release-authority.py` and require a pass.

## Publication binding

- [ ] Confirm the release is built from the canonical, immutable `v<version>` tag.
- [ ] Confirm the verified artifacts are the exact portable assets handed to Velopack for publication.
- [ ] Confirm checksums, SBOM, license inventory, `LICENSE`, and `NOTICE` refer to those exact artifact bytes.
- [ ] Preserve the authority-register result and release evidence with the release record.

The register automation validates structure, completeness, evidence paths, and freshness only. It is not legal advice
or certification. Publication remains blocked while the committed register is unresolved.
