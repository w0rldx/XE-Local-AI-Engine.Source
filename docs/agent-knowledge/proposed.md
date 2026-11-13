# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### A single-process `dotnet test` of XE-Local-AI-Engine.Tests under the build lock blocks every other worktree

**Rule:** never run the whole `XE-Local-AI-Engine.Tests` project as one `dotnet test` under the build lock: it holds the box-wide lock for its whole run (observed about 30 minutes with four waiters). Run that project through `scripts/run-backend-tests.sh` or a `--treenode-filter` scope instead. **Prevents:** one worktree's test run stalling every other worktree's build and gate. **Authority:** `scripts/with-build-lock.sh`, `scripts/run-backend-tests.sh`.
