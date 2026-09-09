import { Stack, Text, Tooltip } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkRepeatStats } from "@/features/benchmarks/models/BenchmarkThroughput";
import { formatLatencyMs, formatStatSummary, formatTokensPerSecond } from "@/features/benchmarks/models/BenchmarkThroughput";

// tg (decode) leads because it is the number an operator compares models by; pp and TTFT ride under it because a fast
// decode over a slow prefill is a different machine from a fast one, and the blended figure this replaced hid that.
// Exact values live in the tooltip so the column stays narrow without rounding away the measurement.
export function ThroughputCell({ run, stats }: { run: BenchmarkRunSummary; stats?: BenchmarkRepeatStats }) {
	const { t } = useTranslation();
	const { ttftMs, promptTokens, promptTokensPerSecond, generationTokens, cachedPromptTokens } = run.throughput;
	// Only shown from two samples up: the spread is the whole point of repeating, and "± 0 (n=1)" would state a
	// certainty a single reading does not have.
	const spread = formatStatSummary(stats?.tokensPerSecond ?? null);
	const tooltip = [
		t("pages.benchmarks.metrics.tgTooltip", "Decode (tg): {{rate}}{{tokens}}", {
			rate: formatTokensPerSecond(run.tokensPerSecond),
			tokens: generationTokens === null ? "" : ` over ${generationTokens} tokens`,
		}),
		t("pages.benchmarks.metrics.ppTooltip", "Prompt (pp): {{rate}}{{tokens}}", {
			rate: formatTokensPerSecond(promptTokensPerSecond),
			tokens: promptTokens === null ? "" : ` over ${promptTokens} tokens`,
		}),
		t("pages.benchmarks.metrics.ttftTooltip", "Time to first token: {{value}}", { value: formatLatencyMs(ttftMs) }),
		// The mode is part of the reading, not a footnote: the same ± means "this machine wobbles" in throughput mode
		// and "this model wanders" in answer-variance mode. Cohorts are split by mode, so one line describes one of them.
		spread === null
			? null
			: t(
					"pages.benchmarks.metrics.spreadTooltip",
					"Across identical launches in {{mode}} mode — tg {{tg}}, pp {{pp}}, TTFT {{ttft}} ms",
					{
						mode: t(`pages.benchmarks.run.repeatModes.${run.repeatMode}`, run.repeatMode),
						tg: spread,
						pp: formatStatSummary(stats?.promptTokensPerSecond ?? null, 0) ?? "—",
						ttft: formatStatSummary(stats?.ttftMs ?? null, 0) ?? "—",
					},
				),
		cachedPromptTokens !== null && cachedPromptTokens > 0
			? t(
					"pages.benchmarks.metrics.cachedTooltip",
					"{{tokens}} prompt tokens came from the KV cache, so the prompt speed is not a cold prefill.",
					{
						tokens: cachedPromptTokens,
					},
				)
			: null,
	]
		.filter((line): line is string => line !== null)
		.join("\n");
	return (
		<Tooltip label={tooltip} multiline={true} w={300} data-testid={`benchmark-throughput-tooltip-${run.id}`}>
			<Stack gap={0} data-testid={`benchmark-throughput-${run.id}`}>
				<Text size="sm">{run.tokensPerSecond?.toFixed(1) ?? "—"}</Text>
				<Text size="xs" c="dimmed">
					{t("pages.benchmarks.metrics.ppAndTtft", "pp {{pp}} · {{ttft}}", {
						pp: promptTokensPerSecond === null ? "—" : promptTokensPerSecond.toFixed(0),
						ttft: formatLatencyMs(ttftMs),
					})}
				</Text>
				{spread === null ? null : (
					<Text size="xs" c="dimmed" data-testid={`benchmark-throughput-spread-${run.id}`}>
						{spread}
					</Text>
				)}
			</Stack>
		</Tooltip>
	);
}
