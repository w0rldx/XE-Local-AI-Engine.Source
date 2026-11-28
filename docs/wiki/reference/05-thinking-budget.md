# Thinking Budget Reference

> Reference page for [Chat](../05-chat.md) "Thinking budget (llama.cpp) and where it is enforceable" · Reviewed: 2026-11-27 · Code-grounded.

It lists where a thinking budget is shown, pinned and bounded in chat and Direct Inference, how reasoning tokens are
counted, and how the benchmarks freeze, store and read a budget. The effort-to-budget ladder is in
[AI.Agent runtime](04-ai-agent-runtime.md); the wire rule is in [llama-server clients](03-llama-server-clients.md).

## Chat and Direct Inference

**The composer shows the budget.** On a local model whose budget is enforceable (`isReasoningControllable`), the
reasoning menu in `ChatComposerToolbar` writes "up to N tokens" beside Low, Medium and High: the node's ladder entry
(`effectiveReasoningBudget`, falling back to the shipped default), or the pinned value on every level while a pin is
set. Off, Auto and any model without an enforceable budget show nothing.

**A per-conversation pin (developer mode).** `NodeChatSamplingOptionsDto.ReasoningBudgetTokens` replaces the effort's
ladder budget. The sampling dialog offers it (Reasoning group) only for an enforceable llama.cpp model. Direct
Inference has the same field (`direct-inference-reasoning-budget`), hidden while Thinking off is set and reset with the
model.

- **Bound.** `ReasoningBudgets.MinTokens` to `MaxTokens` (128 to 131072), checked by
  `InvocationSamplingRules.ValidateReasoningBudget` on every input path: the conversation sampling PATCH, the hub send
  (`NodeChatStreamService`), regenerate (`NodeChatRegenerationService`) and Direct Inference (`DirectInferenceService`).
  The apply path keeps any positive value, because a frozen benchmark replay may carry one below the bound.
- **Only while reasoning is on.** `InvocationAgentFactory` writes no budget when the effective think option is `false`,
  so a pin never overwrites the thinking-off zero budget and never re-enables thinking.
- **Orchestration ignores the pin.** `ParticipantReasoningOptions` reads no sampling.
- **Direct Inference** sends the budget only when the model reports thinking support and an enforceable template;
  otherwise `ReasoningBudgetSkipLog` reports it once and it is omitted.
- **The wire value** is still halved by `ClampToGenerationRoom`.

**Reasoning tokens.** llama-server at the current pin reports no `reasoning_tokens`, so
`DeferredLlamaServerChatClient` counts each round's reasoning text with the server's `/tokenize`. The closing think tag
is not part of `reasoning_content`, so a run stopped at its budget counts exactly budget minus 1. The context-usage
popover (`ContextUsagePopover`, last-round snapshot only) shows "Reasoning tokens" (`providerReasoningTokens`, "not
reported" when null) and "Thinking budget": the applied value, or `requested → applied` when the clamp changed it
(`requestedReasoningBudgetTokens` and `reasoningBudgetTokens` on `NodeChatContextWindowResponse`).

**Unmeasured counts.** When `InvocationRunner.ReclassifyLeakedThinkTextAsync` moves leaked answer text into the reasoning, the provider's count (reasoning channel only) no longer covers it, so the turn's reasoning count reports as unmeasured (null) in the completion, the telemetry totals and the context snapshot; the applied budget is kept. A reasoning round whose usage carries no reasoning count is treated as uncounted on any provider (one that forwards only input and output tokens), and a stream that sends no updates still ends on its own usage. The turn's reasoning total is non-null only when every round that reasoned was counted: a Stop or failure while a reasoning round's count is still pending (`SettlePendingReasoningCount`) reports it as unmeasured, not as the earlier rounds' subtotal. The chat cost ledger falls back to the last round's count only when the runner reported no turn totals at all (`TurnUsageReported`); a reported null total stays null. Direct Inference reports Cancelled, never Done, after a Stop during the count. A tool-using agent reasons once per round and the applied budget is the last round's, so budget sweeps are cleanest with a tool-free agent.

**Answer now.** A streaming request that carries the budget also carries `reasoning_control: true`
(`DeferredLlamaServerChatClient.ApplyReasoningControl`), which arms llama-server's realtime reasoning control (pin
b10201). The client stamps the stream's first update with the server address (`LlamaServerReasoningControl`); its
`ResponseId` is the `chatcmpl-…` id. `InvocationRunner` tracks the turn under its assistant message id in
`InvocationReasoningControl` and records whether the armed completion is still reasoning. The composer's "Answer
now" button (shown beside Stop while a Thoughts segment streams on a local llama.cpp model whose template is
budget-enforceable) posts `chat/messages/{messageId}/answer-now`, which sends
`{"id", "action": "reasoning_end"}` to `/v1/chat/completions/control`: 204 ended, 404 no running turn, 409 not
reasoning, not armed, or refused (the server's own text is logged, never returned). The turn keeps streaming; the
model closes its reasoning and answers.

## Benchmarks

- **The cell includes the budget.** A launch's override or the project's pin is frozen onto each run
  (`reasoningBudgetTokens`), and two budgets of one model are two cells.
- **A launch can override the budget.** `reasoningBudgetTokens` on `runs` and on each `runs/batch` item (echoed on the
  started item) is frozen like the project's pin: the snapshot sampling carries `request ?? project`, and the flat run
  column is set from that same value. `BenchmarkRunFreezeService.FreezeAsync` refuses (400
  `BenchmarkValidationException`) an override that
  - is outside 128 to 131072 or breaks the project's budget rule (`BenchmarkProjectService.ValidateReasoningBudget`);
  - exceeds half a pinned output budget, because `ClampToGenerationRoom` would run it smaller and a sweep would compare
    a budget it never measured;
  - targets a model that cannot enforce a budget;
  - targets an agent whose effort turns thinking off (`ReasoningOptionsResolver.ResolveThinkOption` is `false`; the
    capability check cannot see the agent).

  - without a pinned output budget, exceeds half the project's context window (the room is the window); each round can
    still narrow it by its prompt size, and the run records what applied.

  The project's own pin keeps only the project rule.
- **Grading drops reclassified text.** Text `ReclassifyLeakedThinkTextAsync` moves into the reasoning is cut from the captured output parts (`TrimOutputTo`), so it is not graded as the answer; earlier rounds' text and every reasoning and tool part stay.
- **Enforceability is frozen for unpinned runs too.** It is stamped whether or not a budget is pinned, so an unpinned
  run on a model that cannot honor a budget replays with no effort-ladder budget either. A snapshot frozen before the
  member existed keeps the executor's inert default.

### Run columns

Three flat nullable columns on `benchmark_runs` (migration `AddBenchmarkRunReasoningBudget`):

| Column | Holds |
|---|---|
| `reasoning_budget_tokens` | the budget frozen onto the run (override, else the project's pin); the migration backfilled it from each run's project, since no override existed before |
| `reasoning_tokens` | the primary turn's reasoning tokens (`TurnReasoningTokens`, the per-round counts summed); null when unmeasured |
| `applied_reasoning_budget_tokens` | the budget the provider put on the wire for the turn's last round; null when none or unknown |

Backfilled runs have null in the last two. The migration's `Down` refuses (an `ABORT` raised from a temporary trigger)
when any run holds an override different from its project's budget or a measured value, because dropping them would
silently relabel runs; restore the pre-upgrade database snapshot instead.

### Derived reads

`BenchmarkReasoningReadout` derives two values at read time; neither is stored. Both need a single-round turn, because the applied budget is the last round's while the reasoning count is the turn's sum: zero tool calls and exactly one timed provider request (`Throughput.SegmentCount` is 1), which rules out the reasoning re-prompt a tool count alone cannot. Anything else, or an unknown count, reads null.

- `reasoningAtBudget` is `reasoningTokens >= appliedReasoningBudgetTokens - 1` (`AtBudgetSlackTokens`; the count omits
  the closing think tag). Null when either count is null. It says "at budget", not "cut off".
- `nonReasoningOutputTokens` is `generationTokens - reasoningTokens`, and never negative: null when the reasoning count exceeds the generation count.
- The live stream sends an `OutputTrimmed` event when a reclassification cuts the answer back; its content is the answer kept, so a live view trims its output parts to it.

### Export

Schema version 7 adds each cell's `reasoningBudgetTokens` and each run's `reasoningBudgetTokens`, `reasoningTokens` and
`appliedReasoningBudgetTokens`. The derived reads ride the run JSON but not the CSV, which appends the three run
columns.

### Frontend

In `features/benchmarks/`:

- **Launch matrix** (`BenchmarkLaunchMatrix`): a "Thinking budgets" input, whole numbers from 128 to 131072. Empty runs once with the project's budget and sends no `reasoningBudgetTokens`; each budget typed multiplies the combinations (and counts toward the 100-runs-per-request cap).
- **Combinations table** (`BenchmarkCellsTable`): a Budget column appears only when the listed cells froze more than one budget (`hasBenchmarkBudgetVariants`; a cell with none reads "no budget"). It also shows mean reasoning tokens, mean answer (non-reasoning output) tokens, median time, and "At budget m of n". Every figure is over the answers that reported it (`benchmarkReasoningReadout`): n counts only answers whose at-budget flag is known, so an unknown is never read as "below budget". The item breakdown shows reasoning and answer tokens per answer.
- **Re-run a combination** keeps that combination's budget. The paired-delta pickers name the budget when the cells ran more than one.
- **Repeat statistics** key the cohort on the frozen budget, so a 256 and a 4096 run are never pooled into one mean and deviation; the throughput scatter names each cohort's budget once more than one is present.
- **Quality against budget** (`BenchmarkCharts`) draws from the run list, so it needs no run selected.
- **Run pane** (`BenchmarkRunPane`): the budget line reads "applied N", "applied —" when the applied value was not recorded, or "not applied" when the budget cannot apply (`reasoningBudgetApplicable` false and nothing applied). The row also shows for an unpinned run once an applied budget was recorded (the effort-ladder cap), with the pinned value "—". Reasoning tokens show beside it, with an "at budget" badge.
- **Export buttons** (`BenchmarkExportButtons`) name schema 7.
