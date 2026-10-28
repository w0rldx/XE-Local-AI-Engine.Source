import { useMutation, useQueryClient } from "@tanstack/react-query";

import type { XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkJudgePolicyDraftDto as JudgePolicyDraft } from "@/core/api/generated";
import {
	createBenchmarkProject,
	deleteBenchmarkProject,
	rejudgeBenchmarkProject,
	updateBenchmarkJudgePolicy,
	updateBenchmarkProject,
	updateBenchmarkProjectFidelity,
} from "@/core/api/generated";
import { listBenchmarkProjectsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { toBenchmarkProjectDetail } from "@/features/benchmarks/models/BenchmarkMappers";
import type {
	BenchmarkProjectDetail,
	BenchmarkProjectDraft,
	BenchmarkProjectFidelityDraft,
} from "@/features/benchmarks/models/BenchmarkModels";
import { useBenchmarkInvalidation } from "@/features/benchmarks/queries/useBenchmarks";

// `projectScope` matches every query addressed to one project, whatever its endpoint.
const projectScope = (projectId: string) => [{ tags: ["Benchmarks"], path: { projectId } }] as const;

// A null rubric/referenceAnswer is "the node decides" (default rubric, no reference answer): the member is omitted
// rather than sent as null, so an unset field can never be mistaken for a deliberate blanking.
const projectMutationBody = (draft: BenchmarkProjectDraft) => ({
	name: draft.name,
	coreTask: draft.coreTask,
	contextTokens: draft.contextTokens,
	// Omitted rather than sent as null when unset: an absent budget means context-limited, which the node validates
	// against the context window.
	...(draft.maxOutputTokens === null ? {} : { maxOutputTokens: draft.maxOutputTokens }),
	...(draft.reasoningBudgetTokens === null ? {} : { reasoningBudgetTokens: draft.reasoningBudgetTokens }),
	...(draft.invocationTimeoutSeconds === null ? {} : { invocationTimeoutSeconds: draft.invocationTimeoutSeconds }),
	agentDefinitionId: draft.agentDefinitionId,
	judgeEnabled: draft.judgeEnabled,
	// `judgeMode` is deliberately NOT here: the project write does not carry it, only `PUT .../judge` does. That is
	// also the only path where the mode matters — pairwise needs runs to compare, and a project with runs is frozen,
	// which is exactly when the judge-policy route is the one used.
	judgeModelName: draft.judgeModelName,
	judgeContextTokens: draft.judgeContextTokens,
	...(draft.rubric === null ? {} : { rubric: draft.rubric }),
	...(draft.referenceAnswer === null ? {} : { referenceAnswer: draft.referenceAnswer }),
	fidelityEnabled: draft.fidelityEnabled,
	fidelityKldEnabled: draft.fidelityKldEnabled,
	// Omitted rather than null when unset, exactly as the token budgets above: absent means "the node's default", and
	// the node clamps what it accepts. `fidelityKldBaseFingerprint` is deliberately NOT sent — the node resolves it
	// from the base model, and a caller-supplied one could make two incomparable figures compare equal.
	...(draft.fidelityChunks === null ? {} : { fidelityChunks: draft.fidelityChunks }),
	fidelityKldBaseModelName: draft.fidelityKldBaseModelName,
	requiresReview: draft.requiresReview,
});

export function useCreateBenchmarkProject() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (draft: BenchmarkProjectDraft) => {
			const { data } = await createBenchmarkProject({ body: projectMutationBody(draft) });
			return toBenchmarkProjectDetail(data);
		},
		onSuccess: () => invalidate(),
	});
}

export function useUpdateBenchmarkProject() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({
			projectId,
			expectedVersion,
			draft,
		}: {
			projectId: string;
			expectedVersion: number;
			draft: BenchmarkProjectDraft;
		}) => {
			const { data } = await updateBenchmarkProject({
				path: { projectId },
				body: { ...projectMutationBody(draft), expectedVersion },
			});
			return toBenchmarkProjectDetail(data);
		},
		onSuccess: () => invalidate(),
	});
}

/**
 * Deletes a project with everything under it: its runs and all their results, transcripts and judge verdicts, then its
 * task items, its pairwise fits and its judge-policy history.
 *
 * The node refuses with 409 `ActiveRun` — having deleted nothing — while any of the project's runs is still queued,
 * generating, judging or being compared. Finished runs are deleted with the project; a live one blocks the whole call,
 * so this can never race the run queue. Nothing outside the benchmarks tables points at a project.
 *
 * The invalidation is deliberately `exact`, unlike every other mutation here: the shared {@link useBenchmarkInvalidation}
 * re-reads every project-addressed query, and for a project that no longer exists that is a
 * guaranteed 404 refetch of its own detail while the page still has it selected. Only the LIST is re-read; the page
 * controller then sees the project leave it and selects another one on its own. The deleted project's own entries are
 * dropped from the cache afterwards, once the refreshed list has already moved the selection off it.
 */
export function useDeleteBenchmarkProject() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async ({ projectId, expectedVersion }: { projectId: string; expectedVersion: number }) => {
			await deleteBenchmarkProject({ path: { projectId }, body: { expectedVersion } });
		},
		onSuccess: async (_, { projectId }) => {
			await queryClient.invalidateQueries({ queryKey: listBenchmarkProjectsQueryKey(), exact: true });
			// Scope removal: the project's detail, runs, task items, cells, comparisons and estimates all carry its id
			// in `path`. `inactive` only, because the page may not have re-rendered off the deleted project yet and
			// removing a query that still has an observer makes it refetch — a guaranteed 404 for a project that is
			// gone. What is still mounted becomes inactive on that re-render and is collected with the rest.
			queryClient.removeQueries({ queryKey: projectScope(projectId), type: "inactive" });
		},
	});
}

/** What a judge change did: the refreshed project plus the runs it enqueued for re-judging. */
export interface BenchmarkJudgeChange {
	project: BenchmarkProjectDetail;
	enqueuedRunIds: readonly string[];
	enqueuedRunCount: number;
}

/**
 * The judge policy of a project, editable even while the project is frozen (its task/agent/context are not). A change
 * that would re-score existing runs is refused with 409 `RejudgeRequired` until `confirmRejudge` is set — the caller
 * asks the operator and resends.
 */
export function useUpdateBenchmarkJudgePolicy() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({
			projectId,
			expectedVersion,
			policy,
			confirmRejudge,
		}: {
			projectId: string;
			expectedVersion: number;
			/** null disables judging; existing attempts and revisions stay as history. */
			policy: JudgePolicyDraft | null;
			confirmRejudge: boolean;
		}) => {
			const { data } = await updateBenchmarkJudgePolicy({
				path: { projectId },
				body: { policy, expectedVersion, confirmRejudge },
			});
			const enqueuedRunIds = data.enqueuedRunIds ?? [];
			return { project: toBenchmarkProjectDetail(data.project), enqueuedRunIds, enqueuedRunCount: enqueuedRunIds.length };
		},
		onSuccess: (change) => invalidate(...change.enqueuedRunIds),
	});
}

/** What a fidelity change did: the refreshed project plus the runs it queued a measurement for. */
export interface BenchmarkFidelityChange {
	project: BenchmarkProjectDetail;
	enqueuedCount: number;
}

/**
 * The fidelity settings on their OWN route, with their own CAS. That is what lets them change on a frozen project:
 * the ordinary project write is refused once runs exist, and fidelity is exactly the setting an operator wants to
 * revisit after seeing some.
 *
 * `measureExisting` is opt-in because enabling fidelity should not silently spend GPU on a project's whole history.
 * Pressing it twice queues nothing the second time — runs with an attempt already are skipped by the node.
 */
export function useUpdateBenchmarkProjectFidelity() {
	const invalidate = useBenchmarkInvalidation();
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async ({
			projectId,
			expectedVersion,
			draft,
			measureExisting,
		}: {
			projectId: string;
			expectedVersion: number;
			draft: BenchmarkProjectFidelityDraft;
			measureExisting: boolean;
		}): Promise<BenchmarkFidelityChange> => {
			const { data } = await updateBenchmarkProjectFidelity({
				path: { projectId },
				body: {
					expectedVersion,
					fidelityEnabled: draft.fidelityEnabled,
					fidelityKldEnabled: draft.fidelityKldEnabled,
					// Omitted rather than null when unset, so an absent value means the node's default.
					...(draft.fidelityChunks === null ? {} : { fidelityChunks: draft.fidelityChunks }),
					fidelityKldBaseModelName: draft.fidelityKldBaseModelName,
					measureExisting,
				},
			});
			return { project: toBenchmarkProjectDetail(data.project), enqueuedCount: data.enqueuedCount ?? 0 };
		},
		// The refreshed runs carry their measurement as `queued`, which is what puts the list back on the two-second poll.
		// The response counts the runs it enqueued but does not name them, so an OPEN detail pane — whose own query stopped
		// polling when the run went terminal — is reached by invalidating the run details as a family. Only when something
		// was actually enqueued: a save that measured nothing must not sweep every open pane.
		onSuccess: async (change) => {
			await invalidate();
			if (change.enqueuedCount > 0) {
				await queryClient.invalidateQueries({ queryKey: [{ _id: "getBenchmarkRun" }] });
			}
		},
	});
}

export function useRejudgeBenchmarkProject() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ projectId, expectedVersion }: { projectId: string; expectedVersion: number }) => {
			const { data } = await rejudgeBenchmarkProject({ path: { projectId }, body: { expectedVersion } });
			const enqueuedRunIds = data.enqueuedRunIds ?? [];
			return { project: toBenchmarkProjectDetail(data.project), enqueuedRunIds, enqueuedRunCount: enqueuedRunIds.length };
		},
		onSuccess: (change) => invalidate(...change.enqueuedRunIds),
	});
}
