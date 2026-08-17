import { Alert, Loader } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { toast } from "@/core/ui/notifications/Toast";
import { BenchmarkRunPane } from "@/features/benchmarks/components/BenchmarkRunPane";
import { useBenchmarkRunHub } from "@/features/benchmarks/hooks/useBenchmarkRunHub";
import { applyBenchmarkLiveOverlay } from "@/features/benchmarks/models/BenchmarkModels";
import {
	useBenchmarkRun,
	useCancelBenchmarkRun,
	useClearBenchmarkRunScore,
	useDeleteBenchmarkRun,
	useRejudgeBenchmarkRun,
	useScoreBenchmarkRun,
} from "@/features/benchmarks/queries/useBenchmarks";

export function BenchmarkRunLivePane({ runId }: { runId: string }) {
	const { t } = useTranslation();
	const runQuery = useBenchmarkRun(runId);
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
			<Alert color="red" icon={<IconAlertTriangle size={16} />}>
				{apiErrorMessage(runQuery.error, t("pages.benchmarks.errors.runLoad", "Could not load the run."))}
			</Alert>
		);
	}
	// The durable snapshot stays the mutation target (its `version` is what the optimistic-concurrency writes carry);
	// only what is rendered takes the streamed corrections on top.
	const run = runQuery.data;
	const displayed = applyBenchmarkLiveOverlay(run, live.overlay);
	const scoreErrorToast = async (error: unknown): Promise<void> => {
		await runQuery.refetch();
		toast.error(
			apiErrorMessage(error, t("pages.benchmarks.errors.score", "The run changed before the score was saved. It has been refreshed.")),
		);
	};
	return (
		<BenchmarkRunPane
			run={displayed}
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
