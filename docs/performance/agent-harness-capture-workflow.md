# Agent Harness Capture Workflow

This workflow measures a useful agent task through the production invocation runner rather than benchmarking only the raw model server. It complements the inference-profile benchmark; it does not replace its hardware-fit, VRAM-admission, or raw throughput evidence.

## Safety and evidence boundary

- Use a disposable conversation/project and non-destructive tools.
- Keep the model, quant, runtime build, context allocation, hardware power state, and tool catalog fixed across an A/B comparison.
- Run scenarios sequentially on a single-GPU machine. Concurrent model runs measure contention, not the harness change.
- Do not publish raw logs without review. The `AgentHarnessEfficiency` record is content-free, but neighboring application logs may not be.
- Treat a missing terminal record, failed scenario, model substitution, device-audit failure, or contaminated test run as no evidence.

## Capture surfaces

Every admitted production invocation emits one terminal record through:

1. `chat.invocation.run` activity tags prefixed with `harness.`;
2. `XE.Node` metrics prefixed with `agent_harness_`;
3. one structured `AgentHarnessEfficiency` debug log when the runner's debug logging is enabled.

The record begins at `InvocationRunner.RunAsync` and includes runner duration, readiness and turn-to-first-output latency, provider calls and cumulative provider-round elapsed time, estimated and terminal provider-reported tokens, repeated tool-schema token cost, tool request-to-result latency/result bytes, deterministic context reductions, provider retries, tool-argument repairs, and orchestration participant handoffs. A streamed provider round's elapsed time includes pull backpressure. Tool request-to-result latency begins at the first observed function-call fragment and can include remaining argument generation and function-loop plumbing; it is not presented as delegate execution time. The record never contains prompt/response text, tool identities, arguments/results, paths, or schemas.

For local chat, the total begins before mutation admission and the record separately reports aggregate pre-run admission/context/persistence time plus collision-slot queue time. It does not yet split individual persistence operations, include terminal persistence/post-turn memory extraction, or identify whether a readiness call caused a physical model reload. Current provider abstractions also do not expose cached-prefix token counts, raw usage for every intermediate streaming round, or per-invocation RAM/VRAM peaks. Those fields must not be inferred from zero. Use the existing llama-server cache metric, persistence spans, and GPU smoke/evidence workflow as separate evidence until those seams are correlated.

## Baseline scenarios

Use the same five scenarios before and after each harness change. Run at least five measured repetitions after one unmeasured warm-up; report median and p95, not the best run.

| Scenario | Fixed task shape | Primary evidence |
| --- | --- | --- |
| No tool | One factual question answerable without a tool | Harness overhead, provider calls = 1, TTFT, input/output tokens |
| Single tool | One deterministic read-only tool followed by the answer | Calls per useful tool action, first-tool latency, request-to-result latency/result bytes |
| Multi-step tool | Two dependent read-only tool actions followed by synthesis | Provider rounds, repeated schema cost, repairs/retries, total task time |
| Long context | Same answer task with history near the selected model's context budget | Estimated input, dropped messages, truncated tool results, reliability |
| Repeated prefix | Three turns with unchanged instructions/tool catalog and a small volatile suffix | Warm-turn TTFT/provider latency plus the raw benchmark's cache-rate evidence |

Do not use exact-answer prompts that the selected local model cannot follow reliably. A scenario is valid only if the useful task succeeds; a faster wrong result is a regression.

## Running through Aspire

1. Start the AppHost with the repository's Aspire workflow.
2. Verify the effective device audit and selected model before collecting numbers.
3. Enable the runner debug record for the capture session (for example, set `Serilog__MinimumLevel__Default=Debug` in the temporary launch environment) or export the `XE.Node` metrics/traces from the Aspire dashboard.
4. Execute the scenarios in the table through the normal chat UI/API, never by calling the provider directly.
5. Save the terminal `AgentHarnessEfficiency` record and the scenario success verdict together. The invocation id correlates the record with neighboring traces; it is intentionally not a metric label.
6. Stop the AppHost through the repository lifecycle script/workflow and confirm the resources exited.

## Comparison gates

For each change, compare:

- successful task completion and output quality first;
- total duration and TTFT;
- raw provider-call count;
- cumulative estimated input plus terminal reported input/output tokens;
- repeated tool-schema tokens across all rounds;
- tool-call count, request-to-result latency, failures, repairs, retries, and result bytes;
- deterministic context reductions;
- model readiness/load behavior from the existing node/runtime metrics.

Reject an optimization that wins only by dropping required context, weakening tool validation/sandboxing, increasing retries, or moving foreground work into an unmeasured background call. Promote a default only after it wins on the intended consumer-hardware tier without regressing the others.
