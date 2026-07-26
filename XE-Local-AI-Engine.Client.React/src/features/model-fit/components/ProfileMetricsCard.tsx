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

function formatBoolean(value: boolean, trueLabel: string, falseLabel: string): string {
	return value ? trueLabel : falseLabel;
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

// Renders the role-appropriate metrics of a single benchmark run. Chat reports token throughput and TTFT;
// embedding/reranker report item throughput, latency percentiles, batch shape, and output correctness. Explicit
// global-free and process-budget VRAM readings are kept distinct; legacy effective-free values are fallback-only.
export function ProfileMetricsCard({ metrics, testIdSuffix }: ProfileMetricsCardProps) {
	const { t } = useTranslation();

	const stats: { key: string; label: string; value: string }[] = [];
	if (metrics.role !== null) {
		stats.push({ key: "role", label: t("pages.modelFit.inferenceProfiles.metrics.role", "Role"), value: metrics.role });
	}
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
	if (metrics.itemsPerSecond !== null) {
		stats.push({
			key: "itemsPerSecond",
			label: t("pages.modelFit.inferenceProfiles.metrics.itemsPerSecond", "Items/s"),
			value: formatModelFitMetric(metrics.itemsPerSecond, "items/s", 1),
		});
	}
	if (metrics.inputTokensPerSecond !== null) {
		stats.push({
			key: "inputTokensPerSecond",
			label: t("pages.modelFit.inferenceProfiles.metrics.inputTokensPerSecond", "Input tok/s"),
			value: formatModelFitMetric(metrics.inputTokensPerSecond, "tok/s", 1),
		});
	}
	if (metrics.p50LatencyMs !== null) {
		stats.push({
			key: "p50Latency",
			label: t("pages.modelFit.inferenceProfiles.metrics.p50Latency", "P50 latency"),
			value: formatModelFitMetric(metrics.p50LatencyMs, "ms", 0),
		});
	}
	if (metrics.p95LatencyMs !== null) {
		stats.push({
			key: "p95Latency",
			label: t("pages.modelFit.inferenceProfiles.metrics.p95Latency", "P95 latency"),
			value: formatModelFitMetric(metrics.p95LatencyMs, "ms", 0),
		});
	}
	if (metrics.batchSize !== null) {
		stats.push({ key: "batchSize", label: t("pages.modelFit.inferenceProfiles.metrics.batchSize", "Batch size"), value: metrics.batchSize.toString() });
	}
	if (metrics.outputDimension !== null) {
		stats.push({
			key: "outputDimension",
			label: t("pages.modelFit.inferenceProfiles.metrics.outputDimension", "Output dimension"),
			value: metrics.outputDimension.toString(),
		});
	}
	if (metrics.valuesFinite !== null) {
		stats.push({
			key: "valuesFinite",
			label: t("pages.modelFit.inferenceProfiles.metrics.valuesFinite", "Finite values"),
			value: formatBoolean(
				metrics.valuesFinite,
				t("pages.modelFit.inferenceProfiles.metrics.yes", "Yes"),
				t("pages.modelFit.inferenceProfiles.metrics.no", "No"),
			),
		});
	}
	if (metrics.deterministicOutput !== null) {
		stats.push({
			key: "deterministicOutput",
			label: t("pages.modelFit.inferenceProfiles.metrics.deterministicOutput", "Stable output"),
			value: formatBoolean(
				metrics.deterministicOutput,
				t("pages.modelFit.inferenceProfiles.metrics.yes", "Yes"),
				t("pages.modelFit.inferenceProfiles.metrics.no", "No"),
			),
		});
	}

	const hasExplicitVram =
		metrics.globalFreeVramLoadBytes !== null ||
		metrics.globalFreeVramAfterBytes !== null ||
		metrics.processBudgetVramLoadBytes !== null ||
		metrics.processBudgetVramAfterBytes !== null;
	if (metrics.globalFreeVramLoadBytes !== null) {
		stats.push({
			key: "globalFreeVramLoad",
			label: t("pages.modelFit.inferenceProfiles.metrics.globalFreeVramLoad", "Global free at load"),
			value: formatBytesAsGb(metrics.globalFreeVramLoadBytes),
		});
	}
	if (metrics.globalFreeVramAfterBytes !== null) {
		stats.push({
			key: "globalFreeVramAfter",
			label: t("pages.modelFit.inferenceProfiles.metrics.globalFreeVramAfter", "Global free after"),
			value: formatBytesAsGb(metrics.globalFreeVramAfterBytes),
		});
	}
	if (metrics.processBudgetVramLoadBytes !== null) {
		stats.push({
			key: "processBudgetVramLoad",
			label: t("pages.modelFit.inferenceProfiles.metrics.processBudgetVramLoad", "Process budget at load"),
			value: formatBytesAsGb(metrics.processBudgetVramLoadBytes),
		});
	}
	if (metrics.processBudgetVramAfterBytes !== null) {
		stats.push({
			key: "processBudgetVramAfter",
			label: t("pages.modelFit.inferenceProfiles.metrics.processBudgetVramAfter", "Process budget after"),
			value: formatBytesAsGb(metrics.processBudgetVramAfterBytes),
		});
	}
	if (!hasExplicitVram && metrics.vramLoadBytes !== null) {
		stats.push({ key: "vramLoad", label: t("pages.modelFit.inferenceProfiles.metrics.vramLoad", "VRAM at load"), value: formatBytesAsGb(metrics.vramLoadBytes) });
	}
	if (!hasExplicitVram && metrics.vramAfterBytes !== null) {
		stats.push({ key: "vramAfter", label: t("pages.modelFit.inferenceProfiles.metrics.vramAfter", "VRAM after"), value: formatBytesAsGb(metrics.vramAfterBytes) });
	}

	return (
		<Card withBorder={true} radius="sm" p="md" bg="var(--mantine-color-default-hover)" data-testid={`inference-profile-metrics-${testIdSuffix}`}>
			<Stack gap="sm">
				<Title order={6}>{t("pages.modelFit.inferenceProfiles.metrics.title", "Benchmark metrics")}</Title>
				{metrics.externalPressureDetected ? (
					<Text size="sm" c="red" fw={500} data-testid={`inference-profile-external-pressure-${testIdSuffix}`}>
						{t(
							"pages.modelFit.inferenceProfiles.metrics.externalPressure",
							"External VRAM pressure detected; this benchmark is invalid.",
						)}
					</Text>
				) : null}
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
