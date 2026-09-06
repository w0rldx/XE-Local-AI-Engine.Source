// Server state for the Graph Workflows surface. Every call goes through the generated hey-api `*Options()` /
// `*Mutation()` adapters wrapped in `withResponseValidation`, exactly as `useDevWorkflows.ts` does — no hand-wired
// axios and no hand-written request types. The one exception is the paged event feed, which calls the generated SDK
// fn through `callWithResponseValidation` because hey-api generates no `*InfiniteOptions` for its `afterSeq` cursor.
//
// The generated query keys are single-element arrays `[{ _id: "<operationId>", path, query, … }]`, and TanStack
// matches them by PARTIAL DEEP equality. So `[{ _id, path: { runId } }]` invalidates every cached variant of one
// endpoint for one run while leaving the other runs' caches alone.

import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { listGraphWorkflowRunEvents } from "@/core/api/generated";
import {
	cancelGraphWorkflowRunMutation,
	createGraphWorkflowDefinitionMutation,
	decideGraphWorkflowNodeRunMutation,
	deleteGraphWorkflowDefinitionMutation,
	getGraphWorkflowDefinitionOptions,
	getGraphWorkflowNodeRunOptions,
	getGraphWorkflowRunOptions,
	listAgentDefinitionsOptions,
	listGraphWorkflowDefinitionsOptions,
	listGraphWorkflowRunEventsQueryKey,
	listGraphWorkflowRunsOptions,
	listGraphWorkflowToolsOptions,
	listLocalModelsOptions,
	startGraphWorkflowRunMutation,
	updateGraphWorkflowDefinitionMutation,
	validateGraphWorkflowDefinitionMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { callWithResponseValidation, withResponseValidation } from "@/core/api/ResponseValidation";
import { readGraphWorkflowConflict } from "@/features/graphWorkflows/api/GraphWorkflowConflict";
import type {
	GraphWorkflowRunEventResponse,
	ListGraphWorkflowRunEventsResponse,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

/** Generated operationIds, which are also the generated SDK fn names and the `_id` of every generated query key. */
export const graphWorkflowQueryIds = {
	definitions: "listGraphWorkflowDefinitions",
	definition: "getGraphWorkflowDefinition",
	runs: "listGraphWorkflowRuns",
	run: "getGraphWorkflowRun",
	node: "getGraphWorkflowNodeRun",
	events: "listGraphWorkflowRunEvents",
	tools: "listGraphWorkflowTools",
} as const;

export type GraphWorkflowQueryId = (typeof graphWorkflowQueryIds)[keyof typeof graphWorkflowQueryIds];

/**
 * Partial generated-query-key filter. Without a `path` it matches every cached variant of that endpoint (what a list
 * needs); with one, only the addressed run, definition or node run.
 */
export function graphWorkflowInvalidationKey(
	operationId: string,
	path?: Readonly<Record<string, string>>,
): readonly [{ _id: string; path?: Readonly<Record<string, string>> }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return path ? [{ _id: operationId, path }] : [{ _id: operationId }];
}

/**
 * The run list is a page, and `limit` is a REQUIRED query parameter with no server default. Mirrors
 * `GraphWorkflowRequestLimits.MaxRunPageSize`, the validator's ceiling: the endpoint carries no `definitionId`
 * filter, so a definition's runs are picked out of this page client-side and a short page would hide them.
 */
export const GRAPH_WORKFLOW_RUN_PAGE_SIZE = 200;

interface FeedOptions {
	/** Polling cadence while the hub is unavailable. `undefined` (the live case) means no polling at all. */
	readonly pollIntervalMs?: number;
	readonly enabled?: boolean;
}

function feedQuerySettings(id: string | undefined, options: FeedOptions) {
	return {
		enabled: (options.enabled ?? true) && Boolean(id),
		refetchInterval: options.pollIntervalMs ?? false,
	} as const;
}

/** The picker's definition list. Summaries only — the `graph` itself rides the single-definition read. */
export function useGraphWorkflowDefinitions(options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listGraphWorkflowDefinitionsOptions()),
		enabled: options.enabled ?? true,
	});
}

/**
 * One definition WITH its graph. Read only when a definition is actually open — a definition does not change while an
 * operator edits it, and if it does the `version` on save is what refuses the write, so this never polls.
 */
export function useGraphWorkflowDefinition(definitionId: string | undefined) {
	return useQuery({
		...withResponseValidation(getGraphWorkflowDefinitionOptions({ path: { definitionId: definitionId ?? "" } })),
		enabled: Boolean(definitionId),
	});
}

/**
 * The run list, newest first. The endpoint filters by `status` only, so a definition's runs are selected out of the
 * page here rather than being asked for — one cache entry serves every definition, and a hub ping that invalidates
 * the list refreshes all of them at once.
 */
export function useGraphWorkflowRuns(definitionId?: string, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listGraphWorkflowRunsOptions({ query: { limit: GRAPH_WORKFLOW_RUN_PAGE_SIZE } })),
		enabled: options.enabled ?? true,
		refetchInterval: options.pollIntervalMs ?? false,
		select: (data) => (definitionId ? (data.runs ?? []).filter((run) => run.definitionId === definitionId) : (data.runs ?? [])),
	});
}

/** The run payload: the run summary and one node run per node key of the pinned graph. Backs the whole run view. */
export function useGraphWorkflowRun(runId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(getGraphWorkflowRunOptions({ path: { runId: runId ?? "" } })),
		...feedQuerySettings(runId, options),
	});
}

/** One node run WITH its `input` / `output` / `error` documents, addressed by node KEY rather than by row id. */
export function useGraphWorkflowNodeRun(runId: string | undefined, nodeKey: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(getGraphWorkflowNodeRunOptions({ path: { runId: runId ?? "", nodeKey: nodeKey ?? "" } })),
		...feedQuerySettings(runId && nodeKey ? nodeKey : undefined, options),
	});
}

/** One ascending, deduplicated trail plus the first page's truncation flag. See {@link useGraphWorkflowRunEvents}. */
export interface GraphWorkflowEventFeed {
	readonly events: readonly GraphWorkflowRunEventResponse[];
	readonly replayTruncated: boolean;
}

/**
 * The event log — the one feed that grows without bound, so the one that is cursor-paged. `afterSeq` is an EXCLUSIVE
 * lower bound, the rows come back ascending, and the server caps a page at `EventReplayLimit` (200) reporting
 * `replayTruncated` when it read one row past that cap. There is no `limit` parameter to grow instead.
 *
 * Paging runs FORWARD from sequence 0: the trail is the run's audit history and a run's first event is the one that
 * explains the rest. `lastSeq` is the highest sequence the page actually carried — the server's own resume rule, and
 * exactly what the hub snapshot hands back — so the next cursor is that number. Requiring it to ADVANCE is what stops
 * a "load more" loop if a page ever reports truncation without carrying a higher sequence.
 *
 * Wired onto the generated SDK fn rather than `listGraphWorkflowRunEventsOptions`, because hey-api emits
 * `*InfiniteOptions` only for pagination parameters it recognises and `afterSeq` is not one. The generated query KEY
 * still identifies the cache entry, so `graphWorkflowInvalidationKey(events, { runId })` matches it by partial deep
 * equality and the hub's pings land where they always did.
 *
 * A ping invalidates this query and TanStack then refetches every loaded page in order, recomputing each cursor from
 * the page before it. Events are append-only and never re-stamped, so the cursors are stable.
 */
export function useGraphWorkflowRunEvents(runId: string | undefined, options: FeedOptions = {}) {
	return useInfiniteQuery({
		// The first page's request, which is what this cache entry IS: the whole feed, from the start of the log.
		queryKey: listGraphWorkflowRunEventsQueryKey({ path: { runId: runId ?? "" }, query: { afterSeq: 0 } }),
		initialPageParam: 0,
		getNextPageParam: (lastPage: ListGraphWorkflowRunEventsResponse, _pages, lastPageParam: number) =>
			lastPage.replayTruncated === true && (lastPage.lastSeq ?? 0) > lastPageParam ? lastPage.lastSeq : undefined,
		queryFn: async ({ pageParam, signal }) => {
			const { data } = await callWithResponseValidation(
				listGraphWorkflowRunEvents({ path: { runId: runId ?? "" }, query: { afterSeq: pageParam }, signal, throwOnError: true }),
			);
			return data;
		},
		// One ascending list, deduplicated on the sequence: an overlap at a page boundary — a refetch racing a
		// `fetchNextPage` — must not render a row twice under a repeated React key. Sequences are strictly increasing
		// but NOT contiguous (the counter is shared with the node runs), so this sorts on the number itself.
		//
		// `replayTruncated` comes from the FIRST page: it is the answer to "is this list the whole run", which is what
		// the events tab says out loud rather than presenting a silently short trail as the full history.
		select: (data): GraphWorkflowEventFeed => ({
			events: [
				...new Map(data.pages.flatMap((page) => page.events ?? []).map((event) => [event.seq ?? 0, event])).values(),
			].toSorted((left, right) => (left.seq ?? 0) - (right.seq ?? 0)),
			replayTruncated: data.pages[0]?.replayTruncated === true,
		}),
		...feedQuerySettings(runId, options),
	});
}

/**
 * The tools a Tool node may name. Already filtered server-side to the D6 envelope (`ReadLocal` and no composed
 * approval), so the picker offers exactly the runnable set and must NEVER re-derive eligibility here.
 */
export function useGraphWorkflowTools(options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listGraphWorkflowToolsOptions()),
		enabled: options.enabled ?? true,
	});
}

/**
 * Agent definitions, for an Agent node's optional `agentDefinitionId`. Read straight off the generated client rather
 * than through `features/agents`' own hook: this needs the ids and names and nothing else, and reaching into another
 * feature for a two-field projection is architecture debt the dependency baseline would have to carry forever.
 */
export function useGraphWorkflowAgentOptions(options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listAgentDefinitionsOptions()),
		enabled: options.enabled ?? true,
		select: (data) => (data.items ?? []).map((agent) => ({ value: agent.id, label: agent.name })),
	});
}

/**
 * Chat models on this node, for an Agent node's `model` override. Same filter the chat picker applies (`kind` is
 * `Chat`), because a graph node that names a non-chat model is a run that fails at dispatch.
 */
export function useGraphWorkflowModelOptions(options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listLocalModelsOptions()),
		enabled: options.enabled ?? true,
		select: (data) =>
			(data.items ?? [])
				.filter((model) => model.kind === "Chat")
				.map((model) => ({ value: model.modelName ?? "", label: model.displayLabel ?? model.modelName ?? "" })),
	});
}

function useDefinitionRefresh(): (definitionId?: string) => Promise<void> {
	const queryClient = useQueryClient();
	return async (definitionId?: string): Promise<void> => {
		await queryClient.invalidateQueries({ queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.definitions) });
		if (definitionId) {
			await queryClient.invalidateQueries({
				queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.definition, { definitionId }),
			});
		}
	};
}

/** A new definition. Only the list can be stale — the row it created had no cache entry to invalidate. */
export function useCreateGraphWorkflowDefinition() {
	const refresh = useDefinitionRefresh();
	return useMutation({
		...withResponseValidation(createGraphWorkflowDefinitionMutation()),
		onSuccess: () => refresh(),
	});
}

/**
 * Edit a definition. The body carries the `version` the editor loaded, so a second editor's save is refused with a
 * 409 (`GraphWorkflowDefinitionConflict`) rather than silently overwriting the first — read it with
 * `readGraphWorkflowConflict`. The saved row is re-read because the server bumps `version` and `graphHash`.
 */
export function useUpdateGraphWorkflowDefinition() {
	const refresh = useDefinitionRefresh();
	return useMutation({
		...withResponseValidation(updateGraphWorkflowDefinitionMutation()),
		onSuccess: (_data, variables) => refresh(variables.path?.definitionId),
	});
}

/** A hard delete (the runs keep their pinned graph copy), so the single-definition entry goes with the list. */
export function useDeleteGraphWorkflowDefinition() {
	const refresh = useDefinitionRefresh();
	return useMutation({
		...withResponseValidation(deleteGraphWorkflowDefinitionMutation()),
		onSuccess: (_data, variables) => refresh(variables.path?.definitionId),
	});
}

/**
 * The save-time structural check, run against a graph that has not been persisted. It writes nothing, so it
 * invalidates nothing: its answer is the response body the validation strip renders.
 */
export function useValidateGraphWorkflowDefinition() {
	return useMutation(withResponseValidation(validateGraphWorkflowDefinitionMutation()));
}

/**
 * Starting a run answers 202 with the new run's id: the dispatcher picks the work up out of band, so the run is
 * re-read rather than primed from the response. The id comes off the RESPONSE — there is no run to invalidate before
 * the call — and the list is refreshed because the new run belongs at the top of it.
 */
export function useStartGraphWorkflowRun() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(startGraphWorkflowRunMutation()),
		onSuccess: async (data) => {
			if (data.runId) {
				await queryClient.invalidateQueries({
					queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.run, { runId: data.runId }),
				});
			}
			await queryClient.invalidateQueries({ queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.runs) });
		},
	});
}

/**
 * Cancel is a fire-and-forget 202 — the run reports `Cancelling` until the in-flight nodes drain, which is why the
 * toolbar must not flip straight to the terminal label and why this re-reads rather than priming.
 */
export function useCancelGraphWorkflowRun() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(cancelGraphWorkflowRunMutation()),
		onSuccess: async (_data, variables) => {
			const runId = variables.path?.runId;
			if (runId) {
				await queryClient.invalidateQueries({ queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.run, { runId }) });
			}
			await queryClient.invalidateQueries({ queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.runs) });
		},
	});
}

/**
 * Answering a Pause gate. Invalidation is deliberate rather than optimistic: what follows a decision is the
 * dispatcher's work on its own clock, so the panel re-reads rather than predicting the next state.
 */
export function useDecideGraphWorkflowNodeRun() {
	const queryClient = useQueryClient();

	const refresh = async (runId: string | undefined, nodeKey: string | undefined): Promise<void> => {
		if (!runId) {
			return;
		}
		await queryClient.invalidateQueries({ queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.run, { runId }) });
		await queryClient.invalidateQueries({ queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.events, { runId }) });
		if (nodeKey) {
			await queryClient.invalidateQueries({
				queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.node, { runId, nodeKey }),
			});
		}
	};

	return useMutation({
		...withResponseValidation(decideGraphWorkflowNodeRunMutation()),
		onSuccess: (_data, variables) => refresh(variables.path?.runId, variables.path?.nodeKey),
		// A 409 means the server disagrees with what this panel is showing — the gate was already answered, or the run
		// has moved on. Re-reading is what takes the live buttons away; without it the panel keeps offering a decision
		// on a settled gate, and every further click earns another 409.
		onError: (error, variables) =>
			readGraphWorkflowConflict(error) ? refresh(variables.path?.runId, variables.path?.nodeKey) : undefined,
	});
}
