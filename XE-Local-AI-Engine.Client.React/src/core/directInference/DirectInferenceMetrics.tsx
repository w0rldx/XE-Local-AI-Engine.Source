import { SimpleGrid, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { type DirectInferenceResult, tokensPerSecond } from "@/core/directInference/DirectInferenceModels";
import { formatNumber } from "@/core/formatting/NumberFormatting";

interface DirectInferenceMetricsProps {
	result: DirectInferenceResult;
}

function metric(value: number | null | undefined, digits = 0): string | undefined {
	return value == null ? undefined : formatNumber(value, { maximumFractionDigits: digits });
}

// Usage comes from the provider; the prompt/generation split exists only for llama-server, so a cloud run
// shows "n/a" there rather than a number derived from the wall clock.
export function DirectInferenceMetrics({ result }: DirectInferenceMetricsProps) {
	const { t } = useTranslation();
	const notAvailable = t("pages.directInference.metrics.notAvailable", "n/a");
	const { usage, timings } = result;
	const rows: { key: string; label: string; value: string | undefined }[] = [
		{
			key: "input",
			label: t("pages.directInference.metrics.inputTokens", "Input tokens"),
			value: metric(usage?.inputTokens),
		},
		{
			key: "output",
			label: t("pages.directInference.metrics.outputTokens", "Output tokens"),
			value: metric(usage?.outputTokens),
		},
		{
			key: "reasoning",
			label: t("pages.directInference.metrics.reasoningTokens", "Reasoning tokens"),
			value: metric(usage?.reasoningTokens),
		},
		{
			key: "cached",
			label: t("pages.directInference.metrics.cachedTokens", "Cached tokens"),
			value: metric(usage?.cachedInputTokens ?? timings?.cachedPromptTokens),
		},
		{
			key: "promptMs",
			label: t("pages.directInference.metrics.promptMs", "Prompt ms"),
			value: metric(timings?.promptMs, 1),
		},
		{
			key: "generationMs",
			label: t("pages.directInference.metrics.generationMs", "Generation ms"),
			value: metric(timings?.generationMs, 1),
		},
		{
			key: "promptRate",
			label: t("pages.directInference.metrics.promptRate", "Prompt tok/s"),
			value: metric(tokensPerSecond(timings?.promptTokens, timings?.promptMs), 1),
		},
		{
			key: "generationRate",
			label: t("pages.directInference.metrics.generationRate", "Generation tok/s"),
			value: metric(tokensPerSecond(timings?.generationTokens, timings?.generationMs), 1),
		},
		{
			key: "ttft",
			label: t("pages.directInference.metrics.firstToken", "Time to first token (ms)"),
			value: metric(result.firstTokenMs),
		},
		{ key: "total", label: t("pages.directInference.metrics.total", "Total (ms)"), value: metric(result.elapsedMs) },
	];

	return (
		<SimpleGrid cols={{ base: 2, sm: 3, md: 5 }} spacing="sm" data-testid="direct-inference-metrics">
			{rows.map((row) => (
				<Stack key={row.key} gap={0}>
					<Text size="xs" c="dimmed">
						{row.label}
					</Text>
					<Text size="sm" fw={600} data-testid={`direct-inference-metric-${row.key}`}>
						{row.value ?? notAvailable}
					</Text>
				</Stack>
			))}
		</SimpleGrid>
	);
}
