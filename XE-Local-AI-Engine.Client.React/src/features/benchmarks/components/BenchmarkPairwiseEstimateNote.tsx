import { Alert, Loader, Text } from "@mantine/core";
import { IconAlertTriangle, IconInfoCircle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { formatEstimatedDuration } from "@/features/benchmarks/models/BenchmarkPairwise";
import { useBenchmarkPairwiseEstimate } from "@/features/benchmarks/queries/useBenchmarks";

/**
 * What switching this project to pairwise will actually cost, before the save that commits to it. Pairwise is
 * quadratic and judged in both orders — 12 runs is 132 judge calls — so the number belongs next to the control that
 * causes it, not in a log an hour later.
 */
export function BenchmarkPairwiseEstimateNote({ projectId }: { projectId: string }) {
	const { t } = useTranslation();
	const query = useBenchmarkPairwiseEstimate(projectId);
	const estimate = query.data;

	if (query.isLoading) {
		return <Loader size="xs" data-testid="benchmark-pairwise-estimate-loading" />;
	}
	if (estimate === undefined) {
		return null;
	}

	return (
		<Alert
			color={estimate.warn ? "orange" : "blue"}
			icon={estimate.warn ? <IconAlertTriangle size={16} /> : <IconInfoCircle size={16} />}
			data-testid="benchmark-pairwise-estimate"
		>
			<Text size="sm">
				{t("pages.benchmarks.pairwise.estimate", "{{runs}} runs pair into {{calls}} judge calls.", {
					runs: estimate.pairedRuns,
					calls: estimate.judgeCalls,
				})}
				{/* Omitted entirely when the node cannot estimate one: "0 s" would read as instant. */}
				{estimate.estimatedSeconds === null
					? ""
					: ` ${t("pages.benchmarks.pairwise.estimateDuration", "Roughly {{duration}}.", {
							duration: formatEstimatedDuration(estimate.estimatedSeconds),
						})}`}
			</Text>
			{estimate.cappedRuns > 0 ? (
				<Text size="xs" data-testid="benchmark-pairwise-estimate-capped">
					{t(
						"pages.benchmarks.pairwise.estimateCapped",
						"{{capped}} runs are left out — the cohort caps at {{maximum}}. They rank as pairwise-cap until removed.",
						{ capped: estimate.cappedRuns, maximum: estimate.maximumRuns },
					)}
				</Text>
			) : null}
		</Alert>
	);
}
