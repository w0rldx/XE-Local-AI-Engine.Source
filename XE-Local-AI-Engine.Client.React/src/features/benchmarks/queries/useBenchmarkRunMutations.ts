import { useMutation } from "@tanstack/react-query";

import type {
	XeLocalAiEngineClientEndpointsBenchmarksV1StartBenchmarkRunBatchItem as StartBenchmarkRunBatchItem,
	XeLocalAiEngineClientEndpointsBenchmarksV1StartBenchmarkRunRequest as StartBenchmarkRunRequest,
} from "@/core/api/generated";
import {
	cancelBenchmarkRun,
	clearBenchmarkFidelityCache,
	clearBenchmarkRunScore,
	deleteBenchmarkRun,
	getBenchmarkRunArtifact,
	rejudgeBenchmarkRun,
	scoreBenchmarkRun,
	startBenchmarkRun,
	startBenchmarkRunBatch,
	startBenchmarkRunFidelity,
	submitBenchmarkRunReview,
} from "@/core/api/generated";
import { fileNameFromContentDisposition, saveBlob } from "@/core/api/utils/DownloadBlob";
import { toBenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkMappers";
import type {
	BenchmarkKvCacheType,
	BenchmarkRepeatMode,
	BenchmarkReviewSubmission,
	BenchmarkRunArtifact,
	BenchmarkRunRef,
} from "@/features/benchmarks/models/BenchmarkModels";
import { useBenchmarkInvalidation } from "@/features/benchmarks/queries/useBenchmarks";

export function useStartBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({
			projectId,
			modelName,
			expectedProjectVersion,
			kvCacheType,
			agentDefinitionId,
			repeatMode,
			answerVarianceTemperature,
		}: {
			projectId: string;
			modelName: string;
			expectedProjectVersion: number;
			/** null = Auto: the member is omitted so the node applies its own rule at freeze. */
			kvCacheType: BenchmarkKvCacheType | null;
			/** An agent variant to run instead of the project's agent; omitted = the project's agent. */
			agentDefinitionId?: string;
			repeatMode: BenchmarkRepeatMode;
			/** null = the node's default (0.7). Never sent in throughput mode, which the node samples at 0. */
			answerVarianceTemperature: number | null;
		}) => {
			const body: StartBenchmarkRunRequest = {
				modelName,
				expectedProjectVersion,
				...(kvCacheType === null ? {} : { kvCacheType }),
				...(agentDefinitionId === undefined ? {} : { agentDefinitionId }),
				repeatMode,
				...(repeatMode === "AnswerVariance" && answerVarianceTemperature !== null ? { answerVarianceTemperature } : {}),
			};
			const { data } = await startBenchmarkRun({ path: { projectId }, body });
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.id),
	});
}

/** One matrix cell the node refused, in the same `code` vocabulary a single-run failure uses. */
export interface BenchmarkBatchRejection {
	modelName: string;
	kvCacheType: string | null;
	/** The variant the refused cell asked for; null = the project's own agent. */
	agentDefinitionId: string | null;
	code: string;
	message: string;
}

/** What a batch launch did: the cells that started, and the ones that did not with the reason. */
export interface BenchmarkBatchLaunch {
	startedRunIds: string[];
	rejected: BenchmarkBatchRejection[];
}

/**
 * Launches a whole model × KV-type matrix in one request. Deliberately NOT all-or-nothing — the node reports each cell
 * separately, so an ineligible model comes back as one rejection in a 200 rather than as a failure of the batch. Only a
 * stale project version or a vanished project fails the whole call.
 */
export function useStartBenchmarkRunBatch() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({
			projectId,
			expectedProjectVersion,
			items,
			repeatCount,
			warmup,
			repeatMode,
			answerVarianceTemperature,
		}: {
			projectId: string;
			expectedProjectVersion: number;
			items: StartBenchmarkRunBatchItem[];
			repeatCount: number;
			warmup: boolean;
			repeatMode: BenchmarkRepeatMode;
			answerVarianceTemperature: number | null;
		}): Promise<BenchmarkBatchLaunch & { projectId: string }> => {
			const { data } = await startBenchmarkRunBatch({
				path: { projectId },
				body: {
					expectedProjectVersion,
					items,
					repeatCount,
					warmup,
					repeatMode,
					...(repeatMode === "AnswerVariance" && answerVarianceTemperature !== null ? { answerVarianceTemperature } : {}),
				},
			});
			return {
				projectId,
				startedRunIds: (data.started ?? []).flatMap((item) => item.runIds ?? []),
				rejected: (data.rejected ?? []).map((item) => ({
					modelName: item.modelName,
					kvCacheType: item.kvCacheType ?? null,
					agentDefinitionId: item.agentDefinitionId ?? null,
					code: item.code,
					message: item.message,
				})),
			};
		},
		onSuccess: (result) => invalidate(...result.startedRunIds),
	});
}

export function useCancelBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, target }: { run: BenchmarkRunRef; target: "Primary" | "Judge" }) => {
			const { data } = await cancelBenchmarkRun({ path: { runId: run.id }, body: { target, expectedVersion: run.version } });
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.id),
	});
}

export function useScoreBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, score }: { run: BenchmarkRunRef; score: number }) => {
			const { data } = await scoreBenchmarkRun({ path: { runId: run.id }, body: { score, expectedVersion: run.version } });
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.id),
	});
}

/** Drops the operator override so the run falls back to its judge score (or to unscored). */
export function useClearBenchmarkRunScore() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (run: BenchmarkRunRef) => {
			const { data } = await clearBenchmarkRunScore({ path: { runId: run.id }, body: { expectedVersion: run.version } });
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.id),
	});
}

export function useRejudgeBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, force }: { run: BenchmarkRunRef; force: boolean }) => {
			const { data } = await rejudgeBenchmarkRun({ path: { runId: run.id }, body: { expectedVersion: run.version, force } });
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.id),
	});
}

/**
 * A reviewer's decision on one scoring run of a review project; the latest one wins. The node refuses with 409
 * `NotReviewable` while the judging has not succeeded under the current policy, and with `VersionConflict` when the run
 * moved since it was read.
 */
export function useSubmitBenchmarkRunReview() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, review }: { run: BenchmarkRunRef; review: BenchmarkReviewSubmission }) => {
			const { data } = await submitBenchmarkRunReview({
				path: { runId: run.id },
				body: { ...review, expectedVersion: run.version },
			});
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.id),
	});
}

/**
 * Downloads one verifier artifact. The bytes are the model's own code output, so they are saved as a text file and
 * never rendered: the `blob` override keeps them out of the DOM exactly as the project export does.
 */
export function useBenchmarkRunArtifact() {
	return useMutation({
		mutationFn: async ({ runId, artifact }: { runId: string; artifact: Pick<BenchmarkRunArtifact, "id" | "kind"> }) => {
			const response = await getBenchmarkRunArtifact({ path: { runId, artifactId: artifact.id }, responseType: "blob" });
			saveBlob(
				// The blob override changes the body axios returns, not the generated response type.
				response.data as unknown as Blob,
				fileNameFromContentDisposition(response.headers["content-disposition"]) ?? `${artifact.kind}-${artifact.id}.txt`,
			);
		},
	});
}

/**
 * Re-measures one run's quant fidelity. The node inserts a NEW immutable attempt rather than overwriting: the previous
 * numbers survive a failed re-measure, which is why this is safe to offer as a plain menu action.
 */
export function useStartBenchmarkRunFidelity() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (run: BenchmarkRunRef) => {
			await startBenchmarkRunFidelity({ path: { runId: run.id } });
		},
		onSuccess: (_, run) => invalidate(run.id),
	});
}

/**
 * Drops this project's cached base logits. Nothing measured is lost — the cache is a derived file, and the runs keep
 * the numbers that were computed from it — but the next KLD measurement pays the full base pass again.
 */
export function useClearBenchmarkFidelityCache() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (projectId: string) => {
			await clearBenchmarkFidelityCache({ path: { projectId } });
		},
		onSuccess: () => invalidate(),
	});
}

export function useDeleteBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (run: BenchmarkRunRef) => {
			await deleteBenchmarkRun({ path: { runId: run.id }, body: { expectedVersion: run.version } });
		},
		onSuccess: (_, run) => invalidate(run.id),
	});
}
