import { Loader } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { toast } from "@/core/ui/notifications/Toast";
import { BenchmarkRunPane } from "@/features/benchmarks/components/BenchmarkRunPane";
import { useBenchmarkRunHub } from "@/features/benchmarks/hooks/useBenchmarkRunHub";
import type { BenchmarkReviewSubmission } from "@/features/benchmarks/models/BenchmarkModels";
import {
	applyBenchmarkLiveOverlay,
	benchmarkErrorCode,
	benchmarkScoreControl,
} from "@/features/benchmarks/models/BenchmarkModels";
import { toBenchmarkCriterionKind } from "@/features/benchmarks/models/BenchmarkVerifier";
import {
	useBenchmarkProject,
	useBenchmarkRun,
	useBenchmarkRunArtifact,
	useBenchmarkTaskItems,
	useCancelBenchmarkRun,
	useClearBenchmarkRunScore,
	useDeleteBenchmarkRun,
	useRejudgeBenchmarkRun,
	useScoreBenchmarkRun,
	useSubmitBenchmarkRunReview,
} from "@/features/benchmarks/queries/useBenchmarks";

export function BenchmarkRunLivePane({ runId }: { runId: string }) {
	const { t } = useTranslation();
	const runQuery = useBenchmarkRun(runId);
	// Read through the query key the page already holds, so this costs no request: the judging MODE belongs to the
	// project, not to the run, and the pane below cannot pick its reading without it.
	const projectQuery = useBenchmarkProject(runQuery.data?.projectId ?? null);
	// Read only for a review project, and through the key the page already holds for the selected project, so the
	// scoring check costs no request anywhere else.
	const taskItemsQuery = useBenchmarkTaskItems(projectQuery.data?.requiresReview ? projectQuery.data.id : null);
	const submitReview = useSubmitBenchmarkRunReview();
	const downloadArtifact = useBenchmarkRunArtifact();
	const cancel = useCancelBenchmarkRun();
	const score = useScoreBenchmarkRun();
	const clearScore = useClearBenchmarkRunScore();
	const rejudge = useRejudgeBenchmarkRun();
	const remove = useDeleteBenchmarkRun();
	const live = useBenchmarkRunHub({
		run: runQuery.data,
		refetch: async () => (await runQuery.refetch()).data,
	});
	if (runQuery.isLoading) {
		return <Loader size="sm" />;
	}
	if (!runQuery.data || runQuery.error) {
		return (
			<InlineErrorAlert
				message={apiErrorMessage(runQuery.error, t("pages.benchmarks.errors.runLoad", "Could not load the run."))}
			/>
		);
	}
	// The durable snapshot stays the mutation target (its `version` is what the optimistic-concurrency writes carry);
	// only what is rendered takes the streamed corrections on top.
	const run = runQuery.data;
	const displayed = applyBenchmarkLiveOverlay(run, live.overlay);
	const scoreErrorToast = async (error: unknown): Promise<void> => {
		await runQuery.refetch();
		toast.error(
			benchmarkErrorCode(error) === "ReviewRequired"
				? t("pages.benchmarks.errors.reviewRequired", "This project requires review. Score the run through its review instead.")
				: apiErrorMessage(
						error,
						t("pages.benchmarks.errors.score", "The run changed before the score was saved. It has been refreshed."),
					),
		);
	};
	// The node's refusals are named, not just relayed: each says what the reviewer has to do next.
	const reviewErrorToast = async (error: unknown): Promise<void> => {
		const code = benchmarkErrorCode(error);
		// Both mean the pane shows a run the node has moved past: a newer version, or a judging that changed.
		if (code === "VersionConflict" || code === "NotReviewable") {
			await runQuery.refetch();
		}
		const known: Record<string, string> = {
			NotReviewable: t(
				"pages.benchmarks.errors.notReviewable",
				"This run cannot be reviewed yet. Re-judge it under the current judge policy first.",
			),
			VersionConflict: t(
				"pages.benchmarks.errors.reviewConflict",
				"The run changed before the review was saved. It has been refreshed.",
			),
		};
		toast.error(
			(code === null ? undefined : known[code]) ??
				apiErrorMessage(error, t("pages.benchmarks.errors.review", "Could not save the review.")),
		);
	};
	const project = projectQuery.data;
	const scoreControl = benchmarkScoreControl(project, run, taskItemsQuery.data?.items);
	const review =
		scoreControl === "review"
			? {
					criteria: project?.judge.enabled
						? (project.judge.rubric?.criteria ??
							// A policy on the node's default rubric sends none back; the judging's own criteria are then the rubric.
							run.judge.criteria.map((criterion) => ({ id: criterion.id, title: criterion.id, description: "", weight: 1 })))
						: null,
					isSaving: submitReview.isPending,
					onSubmit: (value: BenchmarkReviewSubmission) =>
						submitReview.mutate({ run, review: value }, { onError: reviewErrorToast }),
				}
			: null;
	return (
		<BenchmarkRunPane
			run={displayed}
			judgeMode={project?.judge.mode}
			review={review}
			scoreControl={scoreControl}
			showSceneViewer={
				project?.judge.rubric?.criteria.some((criterion) => toBenchmarkCriterionKind(criterion.kind) === "sceneSpec") ?? false
			}
			isDownloadingArtifact={downloadArtifact.isPending}
			onDownloadArtifact={(artifact) =>
				downloadArtifact.mutate(
					{ runId: run.id, artifact },
					{
						onError: (error) =>
							toast.error(
								benchmarkErrorCode(error) === "ArtifactCorrupt"
									? t(
											"pages.benchmarks.errors.artifactCorrupt",
											"This evidence file failed its integrity check and was not downloaded.",
										)
									: apiErrorMessage(error, t("pages.benchmarks.errors.artifact", "Could not download this evidence file.")),
							),
					},
				)
			}
			parts={live.parts}
			isConnected={live.isConnected}
			isReconnecting={live.isReconnecting}
			isCancelling={cancel.isPending}
			isScoring={score.isPending || clearScore.isPending}
			isJudgeBusy={cancel.isPending || rejudge.isPending}
			isDeleting={remove.isPending}
			onCancel={(target) =>
				cancel.mutate(
					{ run, target },
					{
						onError: (error) =>
							toast.error(apiErrorMessage(error, t("pages.benchmarks.errors.cancel", "Could not cancel this run phase."))),
					},
				)
			}
			onScore={(value) => score.mutate({ run, score: value }, { onError: scoreErrorToast })}
			onClearScore={() => clearScore.mutate(run, { onError: scoreErrorToast })}
			// An explicit re-judge of one run is forced: the operator asked for a fresh verdict, and the node's
			// idempotent no-op would otherwise answer "already judged under this policy" and do nothing.
			onRejudge={() =>
				rejudge.mutate(
					{ run, force: true },
					{
						onError: (error) =>
							toast.error(apiErrorMessage(error, t("pages.benchmarks.errors.rejudgeRun", "Could not re-judge this run."))),
					},
				)
			}
			onDelete={() =>
				remove.mutate(run, {
					onError: (error) =>
						toast.error(apiErrorMessage(error, t("pages.benchmarks.errors.delete", "Could not delete this terminal run."))),
				})
			}
		/>
	);
}
