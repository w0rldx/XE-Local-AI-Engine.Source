import { Group, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import type { BenchmarkPairwiseRunScore, BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";

export function QualityScoreCell({ run, pairwise }: { run: BenchmarkRunSummary; pairwise?: BenchmarkPairwiseRunScore }) {
	const { t } = useTranslation();
	if (run.qualityScore === null) {
		return <Text size="sm">—</Text>;
	}
	// A fitted score without its interval is not a comparison an operator can make: two runs whose bands overlap are
	// not separated by the difference in their point estimates, however large it looks.
	const interval =
		pairwise === undefined || pairwise.ciLow === null || pairwise.ciHigh === null
			? null
			: `${pairwise.ciLow.toFixed(1)}–${pairwise.ciHigh.toFixed(1)}`;
	return (
		<Group gap={6} wrap="nowrap">
			<Stack gap={0}>
				<Text size="sm" fw={700}>
					{run.qualityScore}
				</Text>
				{interval === null ? null : (
					<Text size="xs" c="dimmed" data-testid={`benchmark-pairwise-ci-${run.id}`}>
						{interval}
					</Text>
				)}
			</Stack>
			<StatusBadge
				color={run.qualityScoreSource === "user" ? "grape" : "blue"}
				label={t(`pages.benchmarks.rank.source.${run.qualityScoreSource}`, run.qualityScoreSource)}
				data-testid={`benchmark-quality-source-${run.id}`}
			/>
		</Group>
	);
}
