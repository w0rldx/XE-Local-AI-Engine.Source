# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### Removing a type can trip the file-placement allowlist, and only a direct test-host run names the entry

**Rule:** after deleting or moving a type out of a multi-type file, run `FilePlacementConventionTests` through the native test host with `--treenode-filter`; `TheAllowlist_HasNoStaleEntry` fails for the now-single-type file, and the gate log shows only the failure, not the stale path. Delete the line from `Architecture/FilePlacementAllowlist.txt`. **Prevents:** a red gate after a clean Debug run, and guessing which allowance went stale. **Authority:** `8655fa109` (BenchmarkKldBaseCache.cs allowance removed), node-settings round 2. Target: backend-tests.md. Pending guard: see Plans retro-actions 2026-10-05, WS4.

### Run CommentBudgetConventionTests in the focused Debug pass before the backend gate

**Rule:** after adding comments or XML docs, run `CommentBudgetConventionTests` with `--treenode-filter` in Debug before queueing `scripts/run-backend-tests.sh`: an own-line `//` run longer than two lines, a `<summary>` over 240 characters, or a now-stale `CommentBudgetAllowlist.txt` entry fails it. **Prevents:** a full Release gate spent to learn a comment is one line too long. **Authority:** `8385432c8` (accessor comments trimmed to the budget), node-settings round 2 S1. Target: backend-tests.md. Pending guard: see Plans retro-actions 2026-10-05, WS4.

### Run the frontend gate on CI's Node major before hand-off, never only on the box's newer Node

**Rule:** CI runs `client-react` on Node 22 (`.github/workflows/build-and-test.yml`, `node-version: 22`) while the development box runs Node 24; a green local `pnpm run acceptance` on 24 is not CI evidence. Run it through `mise exec node@22 -- pnpm run acceptance` (or pin 22 for the gate) before hand-off. **Prevents:** a merge that lands green locally and reds develop on Node-22-only runtime differences: undici in 22 treats jsdom's `Blob` (has `arrayBuffer()`, no `stream()`) as a Blob and throws `object.stream is not a function` for every axios `responseType: "blob"` request MSW's XHR interceptor answers; 24 stringifies it and passes. **Authority:** `src/test/JsdomBlobStream.ts`, `JsdomBlobStream.test.ts`; develop run 37045197138 red after e2fa46635 (image edit), 2026-10-02. Target: frontend-and-api.md. Pending guard: see Plans retro-actions 2026-10-05, WS4.

### Regenerate the OpenAPI client only after a Release build of the contract change

**Rule:** `OPENAPI_LIVE_SCRIPT=openapi scripts/openapi-live-check.sh` starts its host from the Release output with `--no-build`, so a contract change built only in Debug regenerates against the old binaries and reports PASS with no diff. Build the solution in Release first, then confirm the regen diff names the new members. **Prevents:** committing a contract change with a stale client, or reading "no diff" as "nothing to regenerate". **Authority:** `scripts/openapi-live-check.sh` ("Release, --no-build"); model-matrix Track 1a, 2026-10-04. Target: frontend-and-api.md. Pending guard: see Plans retro-actions 2026-10-05, WS4.
