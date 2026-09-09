import { Group, Text, Tooltip } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import type { BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import { rankExclusionAction } from "@/features/benchmarks/models/BenchmarkRanking";

// A missing rank is never left bare: the node says WHY the run is out of the cohort, and the chip carries that reason
// plus the action that would bring it back in.
export function RankCell({ run }: { run: BenchmarkRunSummary }) {
	const { t } = useTranslation();
	if (run.rank !== null) {
		return (
			<Text size="sm" fw={700}>
				{run.rank}
			</Text>
		);
	}
	if (run.rankExclusionReason === null) {
		return <Text size="sm">—</Text>;
	}
	const reason = t(`pages.benchmarks.rank.exclusion.${run.rankExclusionReason}`, run.rankExclusionReason);
	const action = t(`pages.benchmarks.rank.action.${rankExclusionAction(run.rankExclusionReason)}`, "");
	return (
		<Tooltip label={action ? `${reason} — ${action}` : reason} multiline={true} w={260}>
			<Group gap={6} wrap="nowrap">
				<Text size="sm">—</Text>
				<StatusBadge
					color="orange"
					label={t(`pages.benchmarks.rank.exclusionShort.${run.rankExclusionReason}`, run.rankExclusionReason)}
					data-testid={`benchmark-rank-exclusion-${run.id}`}
				/>
			</Group>
		</Tooltip>
	);
}
