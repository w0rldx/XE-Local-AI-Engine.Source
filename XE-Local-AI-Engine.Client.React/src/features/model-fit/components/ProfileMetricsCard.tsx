import { Card, SimpleGrid, Stack, Text, Title } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { formatBytesAsGb, formatModelFitMetric } from "@/features/model-fit/components/ModelFitFormatters";
import type { InferenceBenchmarkMetrics } from "@/features/model-fit/models/InferenceProfileModels";

interface ProfileMetricsCardProps {
	metrics: InferenceBenchmarkMetrics;
	// Stable suffix so each row's card exposes a unique data-testid (the panel renders one card per benchmarked row).
	testIdSuffix: string;
}

// Formats a 0..1 cache-hit ratio as a whole-percent string, or a dash when absent.
function formatCacheHitRate(rate: number | null): string {
	return rate === null ? "—" : `${(rate * 100).toFixed(0)} %`;
}

function Metric({ label, value, testId }: { label: string; value: string; testId: string }) {
	return (
		<Stack gap={0}>
			<Text size="xs" c="dimmed">
				{label}
			</Text>
			<Text size="sm" fw={500} data-testid={testId}>
				{value}
			</Text>
		</Stack>
	);
}

// Renders the metrics of a single benchmark run as outcome stats: TTFT, prompt (PP) tok/s, generation (TG) tok/s,
// cache-hit %, tool-loop ms, and VRAM at load / after. A metric whose value is null is omitted entirely (the stat
// is not rendered) so a sparse run does not show a wall of dashes. Pure presentation over a resolved metrics object.
export function ProfileMetricsCard({ metrics, testIdSuffix }: ProfileMetricsCardProps) {
	const { t } = useTranslation();

	const stats: { key: string; label: string; value: string }[] = [];
	if (metrics.ttftMs !== null) {
		stats.push({ key: "ttft", label: t("pages.modelFit.inferenceProfiles.metrics.ttft", "TTFT"), value: formatModelFitMetric(metrics.ttftMs, "ms", 0) });
	}
	if (metrics.ppTokensPerSecond !== null) {
		stats.push({
			key: "promptTps",
			label: t("pages.modelFit.inferenceProfiles.metrics.promptTps", "Prompt tok/s"),
			value: formatModelFitMetric(metrics.ppTokensPerSecond, "tok/s", 1),
		});
	}
	if (metrics.tokensPerSecond !== null) {
		stats.push({
			key: "genTps",
			label: t("pages.modelFit.inferenceProfiles.metrics.genTps", "Generation tok/s"),
			value: formatModelFitMetric(metrics.tokensPerSecond, "tok/s", 1),
		});
	}
	if (metrics.cacheHitRate !== null) {
		stats.push({ key: "cacheHit", label: t("pages.modelFit.inferenceProfiles.metrics.cacheHit", "Cache hit"), value: formatCacheHitRate(metrics.cacheHitRate) });
	}
	if (metrics.toolLoopMs !== null) {
		stats.push({ key: "toolLoop", label: t("pages.modelFit.inferenceProfiles.metrics.toolLoop", "Tool loop"), value: formatModelFitMetric(metrics.toolLoopMs, "ms", 0) });
	}
	if (metrics.vramLoadBytes !== null) {
		stats.push({ key: "vramLoad", label: t("pages.modelFit.inferenceProfiles.metrics.vramLoad", "VRAM at load"), value: formatBytesAsGb(metrics.vramLoadBytes) });
	}
	if (metrics.vramAfterBytes !== null) {
		stats.push({ key: "vramAfter", label: t("pages.modelFit.inferenceProfiles.metrics.vramAfter", "VRAM after"), value: formatBytesAsGb(metrics.vramAfterBytes) });
	}

	return (
		<Card withBorder={true} radius="sm" p="md" bg="var(--mantine-color-default-hover)" data-testid={`inference-profile-metrics-${testIdSuffix}`}>
			<Stack gap="sm">
				<Title order={6}>{t("pages.modelFit.inferenceProfiles.metrics.title", "Benchmark metrics")}</Title>
				{stats.length > 0 ? (
					<SimpleGrid cols={{ base: 2, sm: 3, md: 4 }} spacing="md">
						{stats.map((stat) => (
							<Metric key={stat.key} label={stat.label} value={stat.value} testId={`inference-profile-metric-${stat.key}-${testIdSuffix}`} />
						))}
					</SimpleGrid>
				) : (
					<Text size="sm" c="dimmed" data-testid={`inference-profile-metrics-empty-${testIdSuffix}`}>
						{t("pages.modelFit.inferenceProfiles.metrics.empty", "This run reported no metrics.")}
					</Text>
				)}
			</Stack>
		</Card>
	);
}
