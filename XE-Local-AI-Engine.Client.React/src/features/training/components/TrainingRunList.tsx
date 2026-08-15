import { Badge, Button, Group, Progress, Stack, Text } from "@mantine/core";
import { IconSchool } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useTrainingRunHub } from "@/features/training/hooks/useTrainingRunHub";
import { isRunActive, runPercent } from "@/features/training/models/TrainingModels";
import { useCancelTrainingRun, useRefreshTrainingRuns, useTrainingRuns } from "@/features/training/queries/useTrainingRuns";

const statusColors: Record<string, string> = {
	Queued: "gray",
	Preparing: "blue",
	Training: "blue",
	Exporting: "blue",
	Smoke: "blue",
	Succeeded: "green",
	Failed: "red",
	Cancelled: "yellow",
};

/**
 * The run list. Exactly one run can be active at a time (a run holds the whole GPU), so only that one subscribes to
 * the progress hub — the rest are static rows and need no live stream at all.
 */
export function TrainingRunList() {
	const { t } = useTranslation();
	const refresh = useRefreshTrainingRuns();
	const runsQuery = useTrainingRuns();
	const runs = useMemo(() => runsQuery.data ?? [], [runsQuery.data]);
	const active = runs.find((run) => isRunActive(run.status)) ?? null;

	// Poll as a floor under the hub while something is in flight; a run can last hours, and a missed push must not
	// leave the list permanently stale.
	const pollingQuery = useTrainingRuns(active != null);
	const live = useTrainingRunHub(active?.id ?? null, refresh);
	const cancelMutation = useCancelTrainingRun();
	const rows = pollingQuery.data ?? runs;

	if (rows.length === 0) {
		return (
			<SectionCard title={t("training.runs.list.title", "Training runs")}>
				<EmptyState
					icon={<IconSchool size={28} opacity={0.5} />}
					message={t("training.runs.list.empty", "No training runs yet. Start one above.")}
					size="sm"
				/>
			</SectionCard>
		);
	}

	return (
		<SectionCard title={t("training.runs.list.title", "Training runs")}>
			<Stack gap="sm">
				{rows.map((run) => {
					const isActive = isRunActive(run.status);
					const step = isActive && live.step > 0 ? live.step : (run.progress?.step ?? 0);
					const totalSteps = isActive && live.totalSteps > 0 ? live.totalSteps : (run.progress?.totalSteps ?? 0);
					const loss = isActive && live.loss != null ? live.loss : (run.progress?.loss ?? null);
					const phase = (isActive ? live.phase : null) ?? run.progress?.phase ?? null;
					const percent = runPercent(step, totalSteps);

					return (
						<Stack gap={4} key={run.id}>
							<Group gap="sm" justify="space-between">
								<Group gap="sm">
									<Badge color={statusColors[run.status] ?? "gray"} variant="light">
										{t(`training.runs.status.${run.status}`, run.status)}
									</Badge>
									{phase == null ? null : (
										<Text c="dimmed" size="sm">
											{t(`training.runs.phase.${phase}`, phase)}
										</Text>
									)}
									{loss == null ? null : (
										<Text size="sm">{t("training.runs.list.loss", "loss {{loss}}", { loss: loss.toFixed(4) })}</Text>
									)}
								</Group>
								{isActive ? (
									<Button
										color="red"
										loading={cancelMutation.isPending}
										onClick={() => cancelMutation.mutate({ path: { runId: run.id } })}
										size="compact-sm"
										variant="subtle"
									>
										{t("training.runs.list.cancel", "Cancel")}
									</Button>
								) : null}
							</Group>

							{percent == null ? null : <Progress animated={isActive} value={percent} />}

							{totalSteps > 0 ? (
								<Text c="dimmed" size="xs">
									{t("training.runs.list.steps", "step {{step}} of {{total}}", { step, total: totalSteps })}
								</Text>
							) : null}

							{run.errorMessage == null ? null : (
								<Text c="red" size="xs">
									{run.errorMessage}
								</Text>
							)}
						</Stack>
					);
				})}
			</Stack>
		</SectionCard>
	);
}
