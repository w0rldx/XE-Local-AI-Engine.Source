import { Button, Group, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { BenchmarkPairwiseEstimateNote } from "@/features/benchmarks/components/BenchmarkPairwiseEstimateNote";

export type BenchmarkConfirmMode = "judgePolicy" | "rejudgeAll" | "pairwise" | null;

interface BenchmarkConfirmationDialogProps {
	mode: BenchmarkConfirmMode;
	projectId?: string;
	affectedRunCount: number;
	isPending: boolean;
	onClose: () => void;
	onConfirm: () => void;
}

/** Presents the explicit confirmation required before a policy-wide re-judge or pairwise launch. */
export function BenchmarkConfirmationDialog({
	mode,
	projectId,
	affectedRunCount,
	isPending,
	onClose,
	onConfirm,
}: BenchmarkConfirmationDialogProps) {
	const { t } = useTranslation();
	return (
		<DialogShell
			opened={mode !== null}
			onClose={onClose}
			title={
				mode === "pairwise"
					? t("pages.benchmarks.project.pairwiseConfirmTitle", "Switch this project to pairwise judging?")
					: t("pages.benchmarks.project.rejudgeConfirmTitle", "Re-judge this project?")
			}
			size="md"
			data-testid="benchmark-rejudge-confirm"
		>
			<Stack gap="md">
				{mode === "pairwise" && projectId ? <BenchmarkPairwiseEstimateNote projectId={projectId} /> : null}
				<Text>
					{mode === "pairwise"
						? t(
								"pages.benchmarks.project.pairwiseConfirm",
								"Every eligible run is judged against every other, in both orders. The comparisons are queued at once and the ranking only appears once the fit completes.",
							)
						: mode === "judgePolicy"
							? t(
									"pages.benchmarks.project.rejudgeConfirmPolicy",
									"Changing the judge re-scores this project. All {{count}} succeeded runs will be re-judged and the ranking is rebuilt from the new cohort.",
									{ count: affectedRunCount },
								)
							: t(
									"pages.benchmarks.project.rejudgeConfirmAll",
									"All {{count}} succeeded runs will be re-judged under the current policy, and the ranked cohort moves to the current judge runtime.",
									{ count: affectedRunCount },
								)}
				</Text>
				<Group justify="flex-end">
					<Button variant="default" onClick={onClose}>
						{t("common.cancel", "Cancel")}
					</Button>
					<Button loading={isPending} onClick={onConfirm} data-testid="benchmark-rejudge-confirm-accept">
						{mode === "pairwise"
							? t("pages.benchmarks.project.pairwiseConfirmAccept", "Switch and queue")
							: t("pages.benchmarks.project.rejudgeConfirmAccept", "Re-judge")}
					</Button>
				</Group>
			</Stack>
		</DialogShell>
	);
}
