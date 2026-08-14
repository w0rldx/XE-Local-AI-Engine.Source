import { Alert, Loader } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { toast } from "@/core/ui/notifications/Toast";
import { BenchmarkRunPane } from "@/features/benchmarks/components/BenchmarkRunPane";
import { useBenchmarkRunHub } from "@/features/benchmarks/hooks/useBenchmarkRunHub";
import {
	useBenchmarkRun,
	useCancelBenchmarkRun,
	useDeleteBenchmarkRun,
	useScoreBenchmarkRun,
} from "@/features/benchmarks/queries/useBenchmarks";

export function BenchmarkRunLivePane({ runId }: { runId: string }) {
	const { t } = useTranslation();
	const runQuery = useBenchmarkRun(runId);
	const cancel = useCancelBenchmarkRun();
	const score = useScoreBenchmarkRun();
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
	const run = runQuery.data;
	return (
		<BenchmarkRunPane
			run={run}
			parts={live.parts}
			isConnected={live.isConnected}
			isReconnecting={live.isReconnecting}
			isCancelling={cancel.isPending}
			isScoring={score.isPending}
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
			onScore={(value) =>
				score.mutate(
					{ run, score: value },
					{
						onError: async (error) => {
							await runQuery.refetch();
							toast.error(
								apiErrorMessage(
									error,
									t("pages.benchmarks.errors.score", "The run changed before the score was saved. It has been refreshed."),
								),
							);
						},
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
