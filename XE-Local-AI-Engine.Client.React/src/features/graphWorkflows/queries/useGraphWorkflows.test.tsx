// @vitest-environment jsdom

// The data layer's wire contract. Three things here fail SILENTLY rather than loudly when they drift, so they are
// pinned: the partial query-key filter (a wrong shape invalidates nothing and the page paints stale rows forever), the
// event feed's forward cursor (`afterSeq` is exclusive, and a cursor that does not advance is a "load more" loop), and
// which keys each mutation drops.
//
// Bodies are hand-built rather than taken from the fixtures wherever the generated zod response validator applies: a
// row id crosses the wire as a GUID, and the fixtures' readable ids (`nr-start`, `ev-1`) would fail validation.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";

import { getGraphWorkflowNodeRunQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { readGraphWorkflowConflict } from "@/features/graphWorkflows/api/GraphWorkflowConflict";
import type { GraphWorkflowGraph } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import {
	GRAPH_WORKFLOW_RUN_PAGE_SIZE,
	graphWorkflowInvalidationKey,
	graphWorkflowQueryIds,
	useCancelGraphWorkflowRun,
	useCreateGraphWorkflowDefinition,
	useDecideGraphWorkflowNodeRun,
	useDeleteGraphWorkflowDefinition,
	useGraphWorkflowAgentOptions,
	useGraphWorkflowDefinition,
	useGraphWorkflowDefinitions,
	useGraphWorkflowModelOptions,
	useGraphWorkflowNodeRun,
	useGraphWorkflowRun,
	useGraphWorkflowRunEvents,
	useGraphWorkflowRuns,
	useGraphWorkflowTools,
	useStartGraphWorkflowRun,
	useUpdateGraphWorkflowDefinition,
	useValidateGraphWorkflowDefinition,
} from "@/features/graphWorkflows/queries/useGraphWorkflows";
import {
	eightNodeGraph,
	graphWorkflowDefinitionSummary,
	graphWorkflowRunSummary,
	graphWorkflowTestIds,
	graphWorkflowTools,
} from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { setupMswServer } from "@/test/UseMswServer";

const runId = graphWorkflowTestIds.run;
const definitionId = graphWorkflowTestIds.definition;
const otherRunId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
const otherDefinitionId = "ffffffff-ffff-4fff-8fff-ffffffffffff";
const operationId = "12345678-1234-4123-8123-123456789abc";

setupMswServer();

function harness(): { queryClient: QueryClient; wrapper: ({ children }: { children: ReactNode }) => ReactNode } {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	return {
		queryClient,
		wrapper: ({ children }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>,
	};
}

interface InvalidateSpy {
	readonly mock: { readonly calls: unknown[][] };
}

function invalidatedKeys(spy: InvalidateSpy): unknown[] {
	return spy.mock.calls.map((call) => (call[0] as { queryKey: unknown }).queryKey);
}

/** The response schema types an event id as a GUID, so a served row has to carry one. */
function eventId(seq: number): string {
	return `${String(seq).padStart(8, "0")}-0000-4000-8000-000000000000`;
}

interface FeedPage {
	readonly seqs: readonly number[];
	readonly replayTruncated: boolean;
}

/**
 * The event feed served BY CURSOR, keyed on the `afterSeq` the client sends and recording every request. `lastSeq` is
 * the highest sequence the page carried — the server's own resume rule — and `replayTruncated` is its "there is more".
 */
function eventFeed(pages: Readonly<Record<string, FeedPage>>): string[] {
	const cursors: string[] = [];
	server.use(
		http.get(localApiPath(`graph-workflows/runs/${runId}/events`), ({ request }) => {
			const afterSeq = new URL(request.url).searchParams.get("afterSeq") ?? "";
			cursors.push(afterSeq);
			const page = pages[afterSeq] ?? { seqs: [], replayTruncated: false };
			return HttpResponse.json({
				events: page.seqs.map((seq) => ({ id: eventId(seq), seq, eventType: "node.started", nodeKey: "analyze", createdAtUtc: seq })),
				lastSeq: page.seqs.at(-1) ?? Number(afterSeq),
				replayTruncated: page.replayTruncated,
			});
		}),
	);
	return cursors;
}

function localModel(overrides: Record<string, unknown>): Record<string, unknown> {
	return {
		modelName: "model",
		displayLabel: null,
		isSelected: false,
		kind: "Chat",
		detectedKind: "Chat",
		capabilities: [],
		isReasoningCapable: false,
		isToolCapable: true,
		isOverridden: false,
		...overrides,
	};
}

function agentDefinition(id: string, name: string): Record<string, unknown> {
	return {
		id,
		name,
		instructions: "Do the thing.",
		kind: "Single",
		allowedToolNames: [],
		toolApprovals: {},
		playbookEnabled: false,
		defaultTemporaryChat: false,
		memoryExtractionEnabled: false,
		disableBaseScaffold: false,
		disableToolRelevanceFilter: false,
		allowedSkillIds: [],
		version: 1,
		createdAtUtc: 1_700_000_000_000,
		updatedAtUtc: 1_700_000_000_000,
	};
}

/** `fetchNextPage` settles React state, so it belongs inside `act`. */
async function loadMore(result: { readonly current: { readonly fetchNextPage: () => Promise<unknown> } }): Promise<void> {
	await act(async () => {
		await result.current.fetchNextPage();
	});
}

describe("graphWorkflowInvalidationKey", () => {
	it("matches every cached variant of one endpoint under one run, and no other run's", async () => {
		const { queryClient } = harness();
		const review = getGraphWorkflowNodeRunQueryKey({ path: { runId, nodeKey: "review" } });
		const lookup = getGraphWorkflowNodeRunQueryKey({ path: { runId, nodeKey: "lookup" } });
		const elsewhere = getGraphWorkflowNodeRunQueryKey({ path: { runId: otherRunId, nodeKey: "review" } });
		for (const key of [review, lookup, elsewhere]) {
			queryClient.setQueryData(key, {});
		}

		// Exactly what the hub does on a `node` ping: one partial key for every node detail under the run, so clicking
		// through the node table never has to re-establish anything.
		await queryClient.invalidateQueries({ queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.node, { runId }) });

		expect(queryClient.getQueryState(review)?.isInvalidated).toBe(true);
		expect(queryClient.getQueryState(lookup)?.isInvalidated).toBe(true);
		expect(queryClient.getQueryState(elsewhere)?.isInvalidated).toBe(false);
	});
});

describe("graph workflow read hooks", () => {
	it("lists the definitions and the server-filtered tool catalogue", async () => {
		server.use(
			http.get(localApiPath("graph-workflows/definitions"), () =>
				HttpResponse.json({ definitions: [graphWorkflowDefinitionSummary()] }),
			),
			http.get(localApiPath("graph-workflows/tools"), () => HttpResponse.json(graphWorkflowTools())),
		);
		const { wrapper } = harness();

		const definitions = renderHook(() => useGraphWorkflowDefinitions(), { wrapper });
		const tools = renderHook(() => useGraphWorkflowTools(), { wrapper });

		await waitFor(() => expect(definitions.result.current.data?.definitions).toHaveLength(1));
		// Already D6-filtered server-side; the hook must hand the list on untouched rather than re-deriving eligibility.
		await waitFor(() => expect(tools.result.current.data?.tools).toHaveLength(8));
	});

	it("asks for a full run page and keeps only the runs of the definition it was given", async () => {
		let limit: string | null = null;
		server.use(
			http.get(localApiPath("graph-workflows/runs"), ({ request }) => {
				limit = new URL(request.url).searchParams.get("limit");
				return HttpResponse.json({
					runs: [graphWorkflowRunSummary(), graphWorkflowRunSummary({ id: otherRunId, definitionId: otherDefinitionId })],
				});
			}),
		);
		const { wrapper } = harness();

		const { result } = renderHook(() => useGraphWorkflowRuns(definitionId), { wrapper });

		// The endpoint carries no `definitionId` filter, so the page must be wide enough for the selection to be honest.
		await waitFor(() => expect(result.current.data).toHaveLength(1));
		expect(result.current.data?.[0]?.id).toBe(runId);
		expect(limit).toBe(`${GRAPH_WORKFLOW_RUN_PAGE_SIZE}`);
	});

	it("keeps only Chat models, because a node naming another kind is a run that fails at dispatch", async () => {
		server.use(
			http.get(localApiPath("models"), () =>
				HttpResponse.json({
					isAvailable: true,
					items: [
						localModel({ modelName: "qwen3", displayLabel: "Qwen 3", kind: "Chat" }),
						localModel({ modelName: "nomic-embed", displayLabel: "Nomic", kind: "Embedding", detectedKind: "Embedding" }),
					],
				}),
			),
		);
		const { wrapper } = harness();

		const { result } = renderHook(() => useGraphWorkflowModelOptions(), { wrapper });

		await waitFor(() => expect(result.current.data).toEqual([{ value: "qwen3", label: "Qwen 3" }]));
	});

	it("projects the agent definitions to picker options", async () => {
		server.use(
			http.get(localApiPath("agents"), () => HttpResponse.json({ items: [agentDefinition(definitionId, "Reviewer")] })),
		);
		const { wrapper } = harness();

		const { result } = renderHook(() => useGraphWorkflowAgentOptions(), { wrapper });

		await waitFor(() => expect(result.current.data).toEqual([{ value: definitionId, label: "Reviewer" }]));
	});

	it("stays idle until it has the id it reads by", () => {
		const { wrapper } = harness();

		const definition = renderHook(() => useGraphWorkflowDefinition(undefined), { wrapper });
		const run = renderHook(() => useGraphWorkflowRun(undefined), { wrapper });
		const nodeRun = renderHook(() => useGraphWorkflowNodeRun(runId, undefined), { wrapper });
		const events = renderHook(() => useGraphWorkflowRunEvents(undefined), { wrapper });

		// MSW is set to error on an unhandled request, so a hook that fired here would fail the test on the URL it asked
		// for — but the fetch status is what says the gate is the reason, rather than a coincidence of timing.
		expect(definition.result.current.fetchStatus).toBe("idle");
		expect(run.result.current.fetchStatus).toBe("idle");
		expect(nodeRun.result.current.fetchStatus).toBe("idle");
		expect(events.result.current.fetchStatus).toBe("idle");
	});
});

describe("useGraphWorkflowRunEvents", () => {
	it("pages FORWARD on the watermark and reports the first page's truncation", async () => {
		const cursors = eventFeed({
			"0": { seqs: [1, 2], replayTruncated: true },
			"2": { seqs: [7, 9], replayTruncated: false },
		});
		const { wrapper } = harness();

		const { result } = renderHook(() => useGraphWorkflowRunEvents(runId), { wrapper });

		await waitFor(() => expect(result.current.hasNextPage).toBe(true));
		// The answer to "is this list the whole run", which the events tab says out loud rather than showing a silently
		// short trail. It comes from the FIRST page and does not change when a later page ends the walk.
		expect(result.current.data?.replayTruncated).toBe(true);
		await loadMore(result);

		await waitFor(() => expect(result.current.data?.events.map((event) => event.seq)).toEqual([1, 2, 7, 9]));
		expect(cursors).toEqual(["0", "2"]);
		expect(result.current.hasNextPage).toBe(false);
		expect(result.current.data?.replayTruncated).toBe(true);
	});

	it("stops rather than looping when a truncated page reports no higher sequence", async () => {
		// The server says "there is more" but hands back nothing above the cursor. Following `lastSeq` blindly would ask
		// for the same page for ever; the walk ends instead.
		eventFeed({ "0": { seqs: [], replayTruncated: true } });
		const { wrapper } = harness();

		const { result } = renderHook(() => useGraphWorkflowRunEvents(runId), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(result.current.hasNextPage).toBe(false);
	});

	it("deduplicates on the sequence when a page overlaps the one before it", async () => {
		// The boundary row served twice — what a refetch racing a `fetchNextPage` produces. Un-deduplicated it is a
		// repeated React key and one event rendered twice in an audit log.
		eventFeed({
			"0": { seqs: [1, 2], replayTruncated: true },
			"2": { seqs: [2, 7], replayTruncated: false },
		});
		const { wrapper } = harness();

		const { result } = renderHook(() => useGraphWorkflowRunEvents(runId), { wrapper });

		await waitFor(() => expect(result.current.hasNextPage).toBe(true));
		await loadMore(result);

		await waitFor(() => expect(result.current.data?.events.map((event) => event.seq)).toEqual([1, 2, 7]));
	});

	it("re-reads every loaded page on a hub-driven invalidation, keeping the merged list intact", async () => {
		const cursors = eventFeed({
			"0": { seqs: [1, 2], replayTruncated: true },
			"2": { seqs: [7], replayTruncated: false },
		});
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useGraphWorkflowRunEvents(runId), { wrapper });

		await waitFor(() => expect(result.current.hasNextPage).toBe(true));
		await loadMore(result);
		await waitFor(() => expect(result.current.data?.events.map((event) => event.seq)).toEqual([1, 2, 7]));

		// Exactly the key every hub ping invalidates.
		await queryClient.invalidateQueries({ queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.events, { runId }) });

		await waitFor(() => expect(cursors).toEqual(["0", "2", "0", "2"]));
		expect(result.current.data?.events.map((event) => event.seq)).toEqual([1, 2, 7]);
	});
});

describe("graph workflow mutations", () => {
	it("refreshes the catalogue and the edited row after a definition write", async () => {
		server.use(
			http.post(localApiPath("graph-workflows/definitions"), () => HttpResponse.json({ id: definitionId }, { status: 201 })),
			http.put(localApiPath(`graph-workflows/definitions/${definitionId}`), () => HttpResponse.json({ id: definitionId })),
			http.delete(localApiPath(`graph-workflows/definitions/${definitionId}`), () => new HttpResponse(null, { status: 204 })),
		);
		const { queryClient, wrapper } = harness();
		const create = renderHook(() => useCreateGraphWorkflowDefinition(), { wrapper });
		const update = renderHook(() => useUpdateGraphWorkflowDefinition(), { wrapper });
		const remove = renderHook(() => useDeleteGraphWorkflowDefinition(), { wrapper });
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		await act(async () => {
			await create.result.current.mutateAsync({ body: { name: "Analyze", graph: eightNodeGraph as GraphWorkflowGraph } });
		});
		expect(invalidatedKeys(invalidate)).toEqual([graphWorkflowInvalidationKey(graphWorkflowQueryIds.definitions)]);

		invalidate.mockClear();
		// The server bumps `version` and `graphHash` on a save, so the edited row is re-read and not only the list: a
		// stale `version` in the editor earns a 409 on the very next save.
		await act(async () => {
			await update.result.current.mutateAsync({ path: { definitionId }, body: { version: 1, graph: eightNodeGraph as GraphWorkflowGraph } });
		});
		expect(invalidatedKeys(invalidate)).toEqual([
			graphWorkflowInvalidationKey(graphWorkflowQueryIds.definitions),
			graphWorkflowInvalidationKey(graphWorkflowQueryIds.definition, { definitionId }),
		]);

		invalidate.mockClear();
		await act(async () => {
			await remove.result.current.mutateAsync({ path: { definitionId } });
		});
		expect(invalidatedKeys(invalidate)).toContainEqual(graphWorkflowInvalidationKey(graphWorkflowQueryIds.definition, { definitionId }));
	});

	it("invalidates nothing when it only validates a graph", async () => {
		server.use(
			http.post(localApiPath("graph-workflows/definitions/validate"), () =>
				HttpResponse.json({ valid: false, errors: [{ key: "review", message: "No route for Approve." }], nodeCount: 8 }),
			),
		);
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useValidateGraphWorkflowDefinition(), { wrapper });
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		await act(async () => {
			await result.current.mutateAsync({ body: { graph: eightNodeGraph as GraphWorkflowGraph } });
		});

		// It writes nothing, so its answer is the response body the validation strip renders — there is no cache to drop.
		expect(invalidate).not.toHaveBeenCalled();
		expect(result.current.data?.errors).toHaveLength(1);
	});

	it("invalidates the NEW run and the run list after a start", async () => {
		server.use(
			http.post(localApiPath(`graph-workflows/definitions/${definitionId}/runs`), () =>
				HttpResponse.json({ runId }, { status: 202 }),
			),
		);
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useStartGraphWorkflowRun(), { wrapper });
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		await act(async () => {
			await result.current.mutateAsync({ path: { definitionId }, body: { requestId: operationId, definitionVersion: 1 } });
		});

		// The run id can only come off the RESPONSE: there was no run to name before the call.
		expect(invalidatedKeys(invalidate)).toEqual([
			graphWorkflowInvalidationKey(graphWorkflowQueryIds.run, { runId }),
			graphWorkflowInvalidationKey(graphWorkflowQueryIds.runs),
		]);
	});

	it("re-reads the run after a cancel rather than flipping the toolbar to a terminal label", async () => {
		server.use(http.post(localApiPath(`graph-workflows/runs/${runId}/cancel`), () => HttpResponse.json({}, { status: 202 })));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useCancelGraphWorkflowRun(), { wrapper });
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		await act(async () => {
			await result.current.mutateAsync({ path: { runId } });
		});

		expect(invalidatedKeys(invalidate)).toEqual([
			graphWorkflowInvalidationKey(graphWorkflowQueryIds.run, { runId }),
			graphWorkflowInvalidationKey(graphWorkflowQueryIds.runs),
		]);
	});

	it("refreshes the run, the node detail and the trail after a decision", async () => {
		server.use(
			http.post(localApiPath(`graph-workflows/runs/${runId}/nodes/review/decide`), () =>
				HttpResponse.json({ decision: "Approve", runStatus: "Running", nodeRunStatus: "Succeeded" }),
			),
		);
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useDecideGraphWorkflowNodeRun(), { wrapper });
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		await act(async () => {
			await result.current.mutateAsync({ path: { runId, nodeKey: "review" }, body: { operationId, decision: "Approve" } });
		});

		expect(invalidatedKeys(invalidate)).toEqual([
			graphWorkflowInvalidationKey(graphWorkflowQueryIds.run, { runId }),
			graphWorkflowInvalidationKey(graphWorkflowQueryIds.events, { runId }),
			graphWorkflowInvalidationKey(graphWorkflowQueryIds.node, { runId, nodeKey: "review" }),
		]);
	});

	it("re-reads on a 409 too, so the panel stops offering a decision the gate has already taken", async () => {
		server.use(
			http.post(localApiPath(`graph-workflows/runs/${runId}/nodes/review/decide`), () =>
				HttpResponse.json(
					{
						type: "about:blank",
						title: "Conflict",
						status: 409,
						detail: "This gate was already decided.",
						conflictType: "GraphWorkflowGateAlreadyDecided",
						standingDecision: "Approve",
					},
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useDecideGraphWorkflowNodeRun(), { wrapper });
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		await act(async () => {
			await result.current
				.mutateAsync({ path: { runId, nodeKey: "review" }, body: { operationId, decision: "Approve" } })
				.catch(() => undefined);
		});

		await waitFor(() => expect(result.current.isError).toBe(true));
		// The same reader the panel uses for its copy, proving the envelope survives the axios interceptor.
		expect(readGraphWorkflowConflict(result.current.error)).toEqual({
			conflictType: "GraphWorkflowGateAlreadyDecided",
			standingDecision: "Approve",
		});
		expect(invalidatedKeys(invalidate)).toContainEqual(graphWorkflowInvalidationKey(graphWorkflowQueryIds.run, { runId }));
	});
});
