// Server state for the Development Workflows surface. Every call goes through the generated hey-api `*Options()` /
// `*Mutation()`, exactly as `useWorkSessions.ts` does — no hand-wired axios, no hand-written request types. The
// paged event feed is in `useDevWorkflowRunEvents.ts`.
//
// The generated query keys are single-element arrays `[{ _id: "<operationId>", path, query, … }]`, and TanStack
// matches them by PARTIAL DEEP equality. So `[{ _id, path: { runId } }]` invalidates every cached variant of one
// endpoint for one run while leaving the other runs' caches alone.

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	archiveDevWorkflowDefinitionMutation,
	cancelDevWorkflowRunMutation,
	createDevWorkflowRuleSetMutation,
	createDevWorkflowWorkItemMutation,
	decideDevWorkflowNodeRunMutation,
	deleteDevWorkflowRuleSetMutation,
	deleteDevWorkflowWorkItemMutation,
	getDevWorkflowArtifactContentOptions,
	getDevWorkflowCapabilityOptions,
	getDevWorkflowDefinitionOptions,
	getDevWorkflowNodeRunOptions,
	getDevWorkflowRuleSetOptions,
	getDevWorkflowRunOptions,
	getDevWorkflowWorkItemOptions,
	listAgentDefinitionsOptions,
	listDevelopmentProjectsOptions,
	listDevWorkflowArtifactsOptions,
	listDevWorkflowDefinitionsOptions,
	listDevWorkflowRuleSetsOptions,
	listDevWorkflowWorkItemsOptions,
	listLocalModelsOptions,
	pauseDevWorkflowRunMutation,
	resumeDevWorkflowRunMutation,
	startDevWorkflowRunMutation,
	updateDevWorkflowDefinitionMutation,
	updateDevWorkflowRuleSetMutation,
	updateDevWorkflowWorkItemMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { readDevWorkflowConflict } from "@/features/devWorkflows/api/DevWorkflowConflict";
import { isActiveDevWorkflowRunStatus, toDevWorkflowRunStatus } from "@/features/devWorkflows/models/DevWorkflowModels";

/** Generated operationIds, which are also the generated SDK fn names and the `_id` of every generated query key. */
export const devWorkflowQueryIds = {
	capability: "getDevWorkflowCapability",
	workItems: "listDevWorkflowWorkItems",
	workItem: "getDevWorkflowWorkItem",
	run: "getDevWorkflowRun",
	node: "getDevWorkflowNodeRun",
	events: "listDevWorkflowRunEvents",
	artifacts: "listDevWorkflowArtifacts",
	artifactContent: "getDevWorkflowArtifactContent",
	definitions: "listDevWorkflowDefinitions",
	definition: "getDevWorkflowDefinition",
	ruleSets: "listDevWorkflowRuleSets",
	ruleSet: "getDevWorkflowRuleSet",
} as const;

export type DevWorkflowQueryId = (typeof devWorkflowQueryIds)[keyof typeof devWorkflowQueryIds];

/**
 * Partial generated-query-key filter. Without a `path` it matches every cached variant of that endpoint (what the
 * list needs); with one, only the addressed run / work item / node run.
 */
export function devWorkflowInvalidationKey(
	operationId: string,
	path?: Readonly<Record<string, string>>,
): readonly [{ _id: string; path?: Readonly<Record<string, string>> }] {
	return path ? [{ _id: operationId, path }] : [{ _id: operationId }];
}

/** The artifact feed is `sinceSeq`-bounded and read whole. */
const FULL_FEED = { sinceSeq: 0 } as const;

/** Work-item list cadence while any listed run is still live. A run-scoped hub cannot feed a list. */
const devWorkflowListPollIntervalMs = 5_000;

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

/**
 * Whether this node serves development workflows at all — the ONE route that still answers when the feature is off,
 * every other one being 404ed by request-path middleware with a bodyless response the client cannot tell from a
 * broken route. Every sibling query on the page is gated on its answer, so a disabled node fires no doomed request.
 * Same shape as `useDevelopmentCapability`, which is the pattern this follows.
 */
export function useDevWorkflowCapability() {
	return useQuery({
		...getDevWorkflowCapabilityOptions(),
		staleTime: 30_000,
	});
}

/**
 * The work-item list. A run-scoped hub cannot feed a list, so this polls at 5s — but only while a listed run
 * is actually live. `latestRunStatus` is null for a work item that has never run, which is exactly a row that cannot
 * change on its own; polling it would be a timer burning for nothing.
 */
export function useDevWorkflowWorkItems(options: FeedOptions = {}) {
	return useQuery({
		...listDevWorkflowWorkItemsOptions(),
		enabled: options.enabled ?? true,
		refetchInterval: (query) => {
			const anyRunLive = (query.state.data?.items ?? []).some(
				(item) => Boolean(item.latestRunStatus) && isActiveDevWorkflowRunStatus(toDevWorkflowRunStatus(item.latestRunStatus)),
			);
			return anyRunLive ? devWorkflowListPollIntervalMs : false;
		},
	});
}

export function useDevWorkflowWorkItem(workItemId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...getDevWorkflowWorkItemOptions({ path: { workItemId: workItemId ?? "" } }),
		...feedQuerySettings(workItemId, options),
	});
}

/** The picker's definition list. Archived templates are hidden — they are not startable. */
export function useDevWorkflowDefinitions(options: FeedOptions = {}) {
	return useQuery({
		...listDevWorkflowDefinitionsOptions({ query: { includeArchived: false } }),
		enabled: options.enabled ?? true,
	});
}

/**
 * One definition WITH its graph, which the list summaries do not carry. Read only when a definition is actually being
 * previewed — a template's shape does not change while an operator looks at it, so this never polls.
 */
export function useDevWorkflowDefinition(definitionId: string | undefined) {
	return useQuery({
		...getDevWorkflowDefinitionOptions({ path: { definitionId: definitionId ?? "" } }),
		enabled: Boolean(definitionId),
	});
}

/** Dev Mode projects, for the create dialog's OPTIONAL project binding. Read through the generated client. */
export function useDevelopmentProjectOptions(options: FeedOptions = {}) {
	return useQuery({
		...listDevelopmentProjectsOptions(),
		enabled: options.enabled ?? true,
	});
}

/** The run payload: status, node-runs and the pinned graph in one call. Backs the whole center pane. */
export function useDevWorkflowRun(runId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...getDevWorkflowRunOptions({ path: { runId: runId ?? "" } }),
		...feedQuerySettings(runId, options),
	});
}

export function useDevWorkflowNodeRun(runId: string | undefined, nodeRunId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...getDevWorkflowNodeRunOptions({ path: { runId: runId ?? "", nodeRunId: nodeRunId ?? "" } }),
		...feedQuerySettings(runId && nodeRunId ? nodeRunId : undefined, options),
	});
}

export function useDevWorkflowArtifacts(runId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...listDevWorkflowArtifactsOptions({ path: { runId: runId ?? "" }, query: FULL_FEED }),
		...feedQuerySettings(runId, options),
	});
}

export function useDevWorkflowArtifactContent(runId: string | undefined, artifactId: string | undefined) {
	return useQuery({
		...getDevWorkflowArtifactContentOptions({ path: { runId: runId ?? "", artifactId: artifactId ?? "" } }),
		enabled: Boolean(runId) && Boolean(artifactId),
	});
}

/**
 * Agent definitions, for the definition editor's per-node agent binding. Read straight off the generated client the
 * way the Dev Mode project list already is, rather than through `features/agents`' own hook: this needs the ids and
 * names and nothing else, and reaching into another feature for a two-field projection is architecture debt the
 * dependency baseline would have to carry forever.
 */
export function useDevWorkflowAgentOptions(options: FeedOptions = {}) {
	return useQuery({
		...listAgentDefinitionsOptions(),
		enabled: options.enabled ?? true,
		select: (data) => (data.items ?? []).map((agent) => ({ id: agent.id, label: agent.name })),
	});
}

/**
 * Chat models on this node, for a node's `modelProfile` override. Same filter the chat picker applies (`kind` is
 * `Chat`), because a graph node that names a non-chat model is a run that fails at dispatch.
 */
export function useDevWorkflowModelOptions(options: FeedOptions = {}) {
	return useQuery({
		...listLocalModelsOptions(),
		enabled: options.enabled ?? true,
		select: (data) =>
			(data.items ?? [])
				.filter((model) => model.kind === "Chat")
				.map((model) => ({ id: model.modelName ?? "", label: model.displayLabel ?? model.modelName ?? "" })),
	});
}

/**
 * Edit a definition, or archive it. Both invalidate the picker's list and the edited definition itself.
 *
 * The update carries the `Version` the edit was made from (an int on the row, no version-history table), so a
 * second editor's save is refused with a 409 rather than silently overwriting the first. Delete is an ARCHIVE —
 * a template a past run pinned a snapshot of must not stop existing, so the row is flagged and hidden from the picker.
 */
export function useDevWorkflowDefinitionMutations() {
	const queryClient = useQueryClient();
	const refresh = async (definitionId?: string): Promise<void> => {
		await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.definitions) });
		if (definitionId) {
			await queryClient.invalidateQueries({
				queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.definition, { definitionId }),
			});
		}
	};

	const update = useMutation({
		...updateDevWorkflowDefinitionMutation(),
		onSuccess: (_data, variables) => refresh(variables.path?.definitionId),
	});
	const archive = useMutation({
		...archiveDevWorkflowDefinitionMutation(),
		onSuccess: (_data, variables) => refresh(variables.path?.definitionId),
	});

	return { update, archive };
}

/**
 * The rule-set catalog. A global resource scoped by `{ projectIds, nodeTypes }` rather than owned by any one
 * run, which is why it is managed from the LIST page and not from a work item. Summaries only — the body is up to
 * 4096 characters of policy prose and is read one rule set at a time.
 */
export function useDevWorkflowRuleSets(options: FeedOptions = {}) {
	return useQuery({
		...listDevWorkflowRuleSetsOptions(),
		enabled: options.enabled ?? true,
	});
}

/** One rule set WITH its body, read only when the editor is actually open on it. */
export function useDevWorkflowRuleSet(ruleSetId: string | undefined) {
	return useQuery({
		...getDevWorkflowRuleSetOptions({ path: { ruleSetId: ruleSetId ?? "" } }),
		enabled: Boolean(ruleSetId),
	});
}

/**
 * Create / update / delete, all three refreshing the catalog. An update also drops the single-rule-set cache entry:
 * the editor reopens on the row it just wrote, and a stale `version` there would earn a 409 on the very next save.
 *
 * Nothing here touches a RUN's cache. `appliedRuleSets` is the resolution a node run persisted at materialization
 * time, so editing a rule set must not change what a past run reports it was told — the "edited since" badge is the
 * whole point of keeping the two apart.
 */
export function useDevWorkflowRuleSetMutations() {
	const queryClient = useQueryClient();
	const refresh = async (ruleSetId?: string): Promise<void> => {
		await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.ruleSets) });
		if (ruleSetId) {
			await queryClient.invalidateQueries({
				queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.ruleSet, { ruleSetId }),
			});
		}
	};

	const create = useMutation({
		...createDevWorkflowRuleSetMutation(),
		onSuccess: () => refresh(),
	});
	const update = useMutation({
		...updateDevWorkflowRuleSetMutation(),
		onSuccess: (_data, variables) => refresh(variables.path?.ruleSetId),
	});
	const remove = useMutation({
		...deleteDevWorkflowRuleSetMutation(),
		onSuccess: (_data, variables) => refresh(variables.path?.ruleSetId),
	});

	return { create, update, remove };
}

export function useCreateDevWorkflowWorkItem() {
	const queryClient = useQueryClient();
	return useMutation({
		...createDevWorkflowWorkItemMutation(),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItems) }),
	});
}

/**
 * Rename a work item, or restate its request. A PATCH: an omitted member is left alone, so the caller sends only what
 * the operator actually changed. Both the detail (the page the edit was made from) and the list (whose card carries
 * the title) are re-read — the response body is not primed into the cache, because the list row carries run counters
 * this write knows nothing about.
 */
export function useUpdateDevWorkflowWorkItem() {
	const queryClient = useQueryClient();
	return useMutation({
		...updateDevWorkflowWorkItemMutation(),
		onSuccess: async (_data, variables) => {
			const workItemId = variables.path?.workItemId;
			if (workItemId) {
				await queryClient.invalidateQueries({
					queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItem, { workItemId }),
				});
			}
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItems) });
		},
	});
}

export function useDeleteDevWorkflowWorkItem() {
	const queryClient = useQueryClient();
	return useMutation({
		...deleteDevWorkflowWorkItemMutation(),
		// The delete removes the runs, their owned work sessions and their artifact bytes, so the caller must navigate
		// away rather than keep a route open on it. Only the list is refreshed here.
		onSuccess: () => queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItems) }),
	});
}

/**
 * Starting a run answers 202: the dispatcher picks the work up out of band, so the work item is re-read rather than
 * primed from the response. The work item is read off the call's own variables because the create dialog starts a run
 * on an item that did not exist when the hook was constructed.
 */
export function useStartDevWorkflowRun() {
	const queryClient = useQueryClient();
	return useMutation({
		...startDevWorkflowRunMutation(),
		onSuccess: async (_data, variables) => {
			const workItemId = variables.path?.workItemId;
			if (workItemId) {
				await queryClient.invalidateQueries({
					queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItem, { workItemId }),
				});
			}
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItems) });
		},
	});
}

/**
 * Pause / resume / cancel. All three are fire-and-forget 202s — the run reports `Pausing` / `Cancelling` until the
 * drain settles, which is why the toolbar must not flip straight to the terminal label.
 */
export function useDevWorkflowRunLifecycle(runId: string | undefined, workItemId: string | undefined) {
	const queryClient = useQueryClient();
	const refresh = async (): Promise<void> => {
		if (runId) {
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.run, { runId }) });
		}
		if (workItemId) {
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItem, { workItemId }) });
		}
		await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItems) });
	};

	const pause = useMutation({ ...pauseDevWorkflowRunMutation(), onSuccess: refresh });
	const resume = useMutation({ ...resumeDevWorkflowRunMutation(), onSuccess: refresh });
	const cancel = useMutation({ ...cancelDevWorkflowRunMutation(), onSuccess: refresh });

	return { pause, resume, cancel };
}

/**
 * The ONE decision surface: a gate answer and a stuck node's Retry/Skip/Abandon travel the same route with the
 * same client-minted `operationId`. Invalidation is deliberate rather than optimistic — what follows a decision is the
 * dispatcher's work on its own clock, so the panel re-reads rather than predicting the next state.
 */
export function useDecideDevWorkflowNodeRun(runId: string | undefined, workItemId: string | undefined) {
	const queryClient = useQueryClient();

	const refresh = async (nodeRunId: string | undefined): Promise<void> => {
		if (runId) {
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.run, { runId }) });
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.events, { runId }) });
			if (nodeRunId) {
				await queryClient.invalidateQueries({
					queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.node, { runId, nodeRunId }),
				});
			}
		}
		if (workItemId) {
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItem, { workItemId }) });
		}
	};

	return useMutation({
		...decideDevWorkflowNodeRunMutation(),
		onSuccess: (_data, variables) => refresh(variables.path?.nodeRunId),
		// A 409 means the server disagrees with what this panel is showing — the gate was already answered, or the node
		// has moved on. Re-reading is what takes the live buttons away; without it the panel keeps offering a decision
		// on a settled gate, and every further click earns another 409.
		onError: (error, variables) => (readDevWorkflowConflict(error) ? refresh(variables.path?.nodeRunId) : undefined),
	});
}
