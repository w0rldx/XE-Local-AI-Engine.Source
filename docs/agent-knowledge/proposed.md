# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### An ArgumentOutOfRangeException from Microsoft.Data.Sqlite on constant SQL is a masked native prepare failure

**Rule:** treat it as SQLITE_MISUSE/NOMEM leaving the tail pointer unset, not as a caller bug. Read the NodeSqliteDiagnostics Warning lines (sqlite3_config_log result code, the pragma's extended result code) before changing pooling or adding locks. Tests that attach a logger match on a unique marker: the native log hook is process-global and every attached logger sees other tests' messages. **Prevents:** chasing a phantom argument bug, and flaky counts from cross-test SQLite messages. **Authority:** `NodeSqlitePragmas` transient open failure; `NodeSqliteDiagnosticsTests`; wiki 08 "Connection pragmas". Target: backend-tests.md or dev-runtime.md.

### Sharing a llama-server binary between two hosts is safe only outside the managed root

**Rule:** a live round beside another running host still gets its own copy of the MANAGED llama-server build (behind `XE_LLAMACPP_SERVER_PATH`); never point two hosts at another host's `source-build/active/build/bin/llama-server`, because the startup `StaleProcessReaper` still claims every llama-server under its managed root by path. A BYO binary outside the root is safe to share: on Linux it is reaped only through this node's spawn receipts (`runtime/llama-server/<pid>.json`), resolved by pid (/proc exe realpath + starttime), never through the name-filtered scan, so a renamed BYO binary is covered. **Prevents:** a resident model vanishing mid-round. **Authority:** `StaleProcessReaper`, `ProcessSpawnReceiptStore`, `LlamaServerProcessSupervisor.SpawnCoreAsync`. Replaces the dev-runtime.md shared-binary entry once approved.

### Budget a pre-flight through the runtime's own first round, and remember what chars/4 cannot see

**Rule:** the outer `ConversationContextBudgeter` counts the system prompt as fixed overhead only when no System message in the history carries it; a pre-flight (benchmark freeze or similar) budgets through `InvocationRunner.BudgetFirstRound`, never a hand-built message list. Tool descriptions and an 18-token per-tool JSON wrapper are counted; the chat template's own tool preamble (~198 tokens on Qwen3.8) is not. **Prevents:** a 2048-token benchmark admitted at freeze and refused at runtime (prompt paid twice, descriptions uncounted: estimate 1189 vs 2052 real). **Authority:** `ConversationContextBudgeterTests` seeded-prompt cases, `InvocationRunnerTests.BudgetFirstRound_*`, `TokenEstimatorCalibrationStore.ToolDefinitionWrapperTokens`; live-findings S5, 2026-09-28.
