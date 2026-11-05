// Server state for the Graph Workflows surface. Every call goes through the generated hey-api `*Options()` /
// `*Mutation()` adapters, exactly as `useDevWorkflows.ts` does — no hand-wired axios and no hand-written request
// types. The paged event feed and the picker option lists sit beside this file.
//
// The generated query keys are single-element arrays `[{ _id: "<operationId>", path, query, … }]`, and TanStack
// matches them by PARTIAL DEEP equality. So `[{ _id, path: { runId } }]` invalidates every cached variant of one
// endpoint for one run while leaving the other runs' caches alone.

import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";

import { exportGraphWorkflowDefinition } from "@/core/api/generated";
import {
	cancelGraphWorkflowRunMutation,
	createGraphWorkflowDefinitionMutation,
	decideGraphWorkflowNodeRunMutation,
	deleteGraphWorkflowDefinitionMutation,
	deleteGraphWorkflowRunMutation,
	getGraphWorkflowCapabilityOptions,
	getGraphWorkflowDefinitionOptions,
	getGraphWorkflowNodeRunOptions,
	getGraphWorkflowRunOptions,
	importGraphWorkflowDefinitionMutation,
	listGraphWorkflowConversationRunsOptions,
	listGraphWorkflowDefinitionsOptions,
	listGraphWorkflowRunsOptions,
	runGraphWorkflowDesignerTurnMutation,
	sendGraphWorkflowChatMessageMutation,
	startGraphWorkflowRunMutation,
	steerGraphWorkflowNodeRunMutation,
	updateGraphWorkflowDefinitionMutation,
	validateGraphWorkflowDefinitionMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { readGraphWorkflowConflict } from "@/features/graphWorkflows/api/GraphWorkflowConflict";

/** Generated operationIds, which are also the generated SDK fn names and the `_id` of every generated query key. */
export const graphWorkflowQueryIds = {
	capability: "getGraphWorkflowCapability",
	definitions: "listGraphWorkflowDefinitions",
	definition: "getGraphWorkflowDefinition",
	runs: "listGraphWorkflowRuns",
	run: "getGraphWorkflowRun",
	node: "getGraphWorkflowNodeRun",
	events: "listGraphWorkflowRunEvents",
	tools: "listGraphWorkflowTools",
	conversationRuns: "listGraphWorkflowConversationRuns",
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
	return path ? [{ _id: operationId, path }] : [{ _id: operationId }];
}

/**
 * The run list is a page, and `limit` is a REQUIRED query parameter with no server default. Mirrors
 * `GraphWorkflowRequestLimits.MaxRunPageSize`, the validator's ceiling: the endpoint carries no `definitionId`
 * filter, so a definition's runs are picked out of this page client-side and a short page would hide them.
 */
export const GRAPH_WORKFLOW_RUN_PAGE_SIZE = 200;

/** The chat reads a conversation's newest bound runs only; the endpoint's own default page. Older runs show no activity. */
export const GRAPH_WORKFLOW_CONVERSATION_RUN_PAGE_SIZE = 20;

export interface FeedOptions {
	/** Polling cadence while the hub is unavailable. `undefined` (the live case) means no polling at all. */
	readonly pollIntervalMs?: number;
	readonly enabled?: boolean;
}

export function feedQuerySettings(id: string | undefined, options: FeedOptions) {
	return {
		enabled: (options.enabled ?? true) && Boolean(id),
		refetchInterval: options.pollIntervalMs ?? false,
	} as const;
}

/** The picker's definition list. Summaries only — the `graph` itself rides the single-definition read. */
export function useGraphWorkflowDefinitions(options: FeedOptions = {}) {
	return useQuery({
		...listGraphWorkflowDefinitionsOptions(),
		enabled: options.enabled ?? true,
	});
}

/**
 * One definition WITH its graph. Read only when a definition is actually open — a definition does not change while an
 * operator edits it, and if it does the `version` on save is what refuses the write, so this never polls.
 */
export function useGraphWorkflowDefinition(definitionId: string | undefined) {
	return useQuery({
		...getGraphWorkflowDefinitionOptions({ path: { definitionId: definitionId ?? "" } }),
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
		...listGraphWorkflowRunsOptions({ query: { limit: GRAPH_WORKFLOW_RUN_PAGE_SIZE } }),
		enabled: options.enabled ?? true,
		refetchInterval: options.pollIntervalMs ?? false,
		select: (data) => (definitionId ? (data.runs ?? []).filter((run) => run.definitionId === definitionId) : (data.runs ?? [])),
	});
}

/** The run payload: the run summary and one node run per node key of the pinned graph. Backs the whole run view. */
export function useGraphWorkflowRun(runId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...getGraphWorkflowRunOptions({ path: { runId: runId ?? "" } }),
		...feedQuerySettings(runId, options),
	});
}

/** One node run WITH its `input` / `output` / `error` documents, addressed by node KEY rather than by row id. */
export function useGraphWorkflowNodeRun(runId: string | undefined, nodeKey: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...getGraphWorkflowNodeRunOptions({ path: { runId: runId ?? "", nodeKey: nodeKey ?? "" } }),
		...feedQuerySettings(runId && nodeKey ? nodeKey : undefined, options),
	});
}

/**
 * The documents of several SETTLED node runs of one run — what the chat's activity block reads for the nodes that
 * produced something worth showing. Same cache entries as {@link useGraphWorkflowNodeRun}. A settled node's document
 * never changes again within one attempt, so each is read once per attempt: no refetch on a remount (hiding and showing
 * the activity), no poll, and the hub skips it on a node ping. Answers the documents that have landed, keyed by node key.
 */
export function useSettledGraphWorkflowNodeRuns(
	runId: string | undefined,
	nodeRuns: readonly { readonly nodeKey: string; readonly attempt: number }[],
) {
	// "Settled" is per ATTEMPT: the dispatcher retries a retryable failure IN PLACE (same node run, attempt + 1), so a
	// cached Failed document goes out of date the moment the run detail reports a later attempt. Such a document counts
	// as stale — refetched on the next mount (the node leaves these keys while it re-runs, so the query unmounts) and on
	// an options change while mounted — and is withheld until the new attempt's document lands.
	const isCurrent = (data: { attempt: number } | undefined, attempt: number): boolean =>
		data !== undefined && data.attempt >= attempt;
	return useQueries({
		queries: nodeRuns.map(({ nodeKey, attempt }) => ({
			...getGraphWorkflowNodeRunOptions({ path: { runId: runId ?? "", nodeKey } }),
			enabled: Boolean(runId),
			staleTime: (query: { state: { data?: { attempt: number } } }) =>
				isCurrent(query.state.data, attempt) ? Number.POSITIVE_INFINITY : 0,
		})),
		combine: (results) =>
			new Map(
				results.flatMap((result, index) =>
					result.data?.nodeKey && isCurrent(result.data, nodeRuns[index]?.attempt ?? 0)
						? [[result.data.nodeKey, result.data] as const]
						: [],
				),
			),
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
		...createGraphWorkflowDefinitionMutation(),
		onSuccess: () => refresh(),
	});
}

/** A definition created from a share file; like a create, only the list can be stale. */
export function useImportGraphWorkflowDefinition() {
	const refresh = useDefinitionRefresh();
	return useMutation({
		...importGraphWorkflowDefinitionMutation(),
		onSuccess: () => refresh(),
	});
}

/**
 * The SAVED definition as a share file. A mutation rather than a query: it runs once per click, and caching a
 * download would hand back an export of a version that has since been saved over.
 */
export function useExportGraphWorkflowDefinition() {
	return useMutation({
		mutationFn: async (options: { readonly path: { readonly definitionId: string } }) =>
			(await exportGraphWorkflowDefinition({ path: options.path })).data,
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
		...updateGraphWorkflowDefinitionMutation(),
		onSuccess: (_data, variables) => refresh(variables.path?.definitionId),
	});
}

/** A hard delete (the runs keep their pinned graph copy), so the single-definition entry goes with the list. */
export function useDeleteGraphWorkflowDefinition() {
	const refresh = useDefinitionRefresh();
	return useMutation({
		...deleteGraphWorkflowDefinitionMutation(),
		onSuccess: (_data, variables) => refresh(variables.path?.definitionId),
	});
}

/**
 * The save-time structural check, run against a graph that has not been persisted. It writes nothing, so it
 * invalidates nothing: its answer is the response body the validation strip renders.
 */
export function useValidateGraphWorkflowDefinition() {
	return useMutation(validateGraphWorkflowDefinitionMutation());
}

/**
 * One designer turn. It writes nothing on the server, so it invalidates nothing. The caller passes a `signal` with the
 * body, which aborts the request: the assistant's Cancel. Never retried: a turn holds the node's single draft slot for
 * seconds to minutes, and the failures a retry would meet again (busy node, refused cloud model) are the operator's to
 * act on.
 */
export function useGraphWorkflowDesignerTurn() {
	return useMutation({ ...runGraphWorkflowDesignerTurnMutation(), retry: false });
}

/**
 * Starting a run answers 202 with the new run's id: the dispatcher picks the work up out of band, so the run is
 * re-read rather than primed from the response. The id comes off the RESPONSE — there is no run to invalidate before
 * the call — and the list is refreshed because the new run belongs at the top of it.
 */
export function useStartGraphWorkflowRun() {
	const queryClient = useQueryClient();
	return useMutation({
		...startGraphWorkflowRunMutation(),
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
 * Whether this node serves graph workflows at all — the ONE route that still answers when the operator switched the
 * feature off (`GraphWorkflows:Enabled=false`), every other one being 404ed with a bodyless response the client cannot
 * tell from a broken route. The route gate, the nav entry and chat workflow mode read it, so a disabled node hides the
 * feature instead of reporting load failures. Same shape as `useDevWorkflowCapability`.
 */
export function useGraphWorkflowCapability(options: { readonly enabled?: boolean } = {}) {
	return useQuery({
		...getGraphWorkflowCapabilityOptions(),
		enabled: options.enabled ?? true,
		staleTime: 30_000,
	});
}

/**
 * The runs bound to one chat conversation, newest first, each with what the chat page renders it from (definition
 * name, trigger message, a parked ChatInput's prompt). This is what puts a reloaded conversation back into workflow
 * mode. A node with Graph Workflows switched off answers 404, which the chat reads as "no bound runs".
 */
export function useGraphWorkflowConversationRuns(conversationId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...listGraphWorkflowConversationRunsOptions({
			path: { conversationId: conversationId ?? "" },
			query: { limit: GRAPH_WORKFLOW_CONVERSATION_RUN_PAGE_SIZE },
		}),
		...feedQuerySettings(conversationId, options),
		select: (data) => data.runs ?? [],
	});
}

/**
 * A chat message into workflow mode: starts the selected Chat workflow, or answers the bound run's parked ChatInput.
 * The server persists the user message itself, so the conversation and its bound-run list are re-read rather than
 * primed. Refusals arrive as 409s (`runBusy`, `rerunConfirmationRequired`, `attachmentsNotAccepted`) that the caller
 * reads with `readGraphWorkflowConflict`.
 */
export function useSendGraphWorkflowChatMessage() {
	const queryClient = useQueryClient();
	return useMutation({
		...sendGraphWorkflowChatMessageMutation(),
		onSuccess: async (_data, variables) => {
			const conversationId = variables.path?.conversationId;
			if (conversationId) {
				await queryClient.invalidateQueries({
					queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.conversationRuns, { conversationId }),
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
		...cancelGraphWorkflowRunMutation(),
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
 * A hard delete of a finished run (a live one answers 409). The run's own entries are dropped rather than refetched,
 * which would only 404, and the lists that showed it are re-read.
 */
export function useDeleteGraphWorkflowRun() {
	const queryClient = useQueryClient();
	return useMutation({
		...deleteGraphWorkflowRunMutation(),
		onSuccess: async (_data, variables) => {
			const runId = variables.path?.runId;
			if (runId) {
				for (const id of [graphWorkflowQueryIds.run, graphWorkflowQueryIds.node, graphWorkflowQueryIds.events]) {
					queryClient.removeQueries({ queryKey: graphWorkflowInvalidationKey(id, { runId }) });
				}
			}
			await queryClient.invalidateQueries({ queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.runs) });
			await queryClient.invalidateQueries({ queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.conversationRuns) });
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
		...decideGraphWorkflowNodeRunMutation(),
		onSuccess: (_data, variables) => refresh(variables.path?.runId, variables.path?.nodeKey),
		// A 409 means the server disagrees with what this panel is showing — the gate was already answered, or the run
		// has moved on. Re-reading is what takes the live buttons away; without it the panel keeps offering a decision
		// on a settled gate, and every further click earns another 409.
		onError: (error, variables) =>
			readGraphWorkflowConflict(error) ? refresh(variables.path?.runId, variables.path?.nodeKey) : undefined,
	});
}

/**
 * Steering a running Agent / LLM Call (wiki page 21 §3.7). 202 means the intent is committed; the dispatcher applies it on its next
 * tick, so this re-reads the run, its trail, the node and the bound-run list rather than predicting the reset.
 */
export function useSteerGraphWorkflowNodeRun() {
	const queryClient = useQueryClient();
	return useMutation({
		...steerGraphWorkflowNodeRunMutation(),
		onSuccess: async (_data, variables) => {
			const { runId, nodeKey } = variables.path;
			await Promise.all([
				queryClient.invalidateQueries({ queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.run, { runId }) }),
				queryClient.invalidateQueries({ queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.events, { runId }) }),
				queryClient.invalidateQueries({
					queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.node, { runId, nodeKey }),
				}),
				queryClient.invalidateQueries({ queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.conversationRuns) }),
			]);
		},
	});
}
