import { BarChart, LineChart, ScatterChart } from "@mantine/charts";
import { Stack, Text } from "@mantine/core";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import {
	fidelityBarSeries,
	hasChartableRuns,
	reasoningBudgetSeries,
	speedBarSeries,
	throughputScatterSeries,
} from "@/features/benchmarks/models/BenchmarkChartData";
import type { BenchmarkRunDetail, BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import { formatStatSummary } from "@/features/benchmarks/models/BenchmarkThroughput";

// Built on `@mantine/charts` — the app's existing charting layer (the usage dashboard is the other user), which is
// recharts underneath. Reusing it rather than driving recharts directly buys the theme-aware frame, tooltip and legend
// this surface would otherwise hand-roll, and keeps one charting convention in the client instead of two.
//
// Every axis here is DISPLAY ONLY. Throughput, perplexity and KL divergence never enter `rank`, and no panel orders
// anything by them.

/**
 * The categorical order, fixed and never cycled: a seventh series is left out rather than repainted as the first,
 * because two series sharing a colour is a chart that lies about identity.
 *
 * Each slot names its light and dark value through CSS `light-dark()` rather than a Mantine shade token, because a
 * shade token resolves to ONE hex in both schemes. The two rows are validated independently against their own surface
 * — same hue, different step — for lightness band, chroma, colour-vision separation and contrast. The light row
 * carries a sub-3:1 contrast note on its warm and green slots, discharged by the relief this surface already has:
 * every series is named in the legend, every point is labelled in its tooltip, and the runs table above is the same
 * numbers as text.
 */
const seriesColors = [
	"light-dark(#228be6, #339af0)",
	"light-dark(#fd7e14, #e8590c)",
	"light-dark(#12b886, #0ca678)",
	"light-dark(#be4bdb, #cc5de8)",
	"light-dark(#82c91e, #66a80f)",
	"light-dark(#e64980, #e64980)",
] as const;

const chartHeight = 240;
const oneDecimal = (value: number): string => value.toFixed(1);

/** Panel wrapper: one title, one optional note, one plot. */
function ChartPanel({ title, note, children }: { title: string; note?: string; children: React.ReactNode }) {
	return (
		<Stack gap={4}>
			<Text size="sm" fw={600}>
				{title}
			</Text>
			{note === undefined ? null : (
				<Text size="xs" c="dimmed">
					{note}
				</Text>
			)}
			{children}
		</Stack>
	);
}

/**
 * Every measured repeat as its own point, one series per repeat cohort — the same model build, KV type, launch
 * identity and repeat mode. Cohorts rather than models, because two runs of one model on different launch arguments
 * are two different experiments and one cloud over both would show a spread that is really a configuration difference.
 */
function ThroughputScatter({ runs }: { runs: readonly BenchmarkRunSummary[] }) {
	const { t } = useTranslation();
	const series = useMemo(() => throughputScatterSeries(runs), [runs]);
	if (series.length === 0) {
		return null;
	}
	const shown = series.slice(0, seriesColors.length);
	const hidden = series.length - shown.length;
	return (
		<ChartPanel
			title={t("pages.benchmarks.charts.throughputTitle", "Decode speed per repeat")}
			note={
				hidden > 0
					? t("pages.benchmarks.charts.cohortsHidden", "Showing {{shown}} of {{total}} repeat cohorts.", {
							shown: shown.length,
							total: series.length,
						})
					: undefined
			}
		>
			<ScatterChart
				h={chartHeight}
				data={shown.map((cohort, index) => ({
					// The cohort's mean and sigma ride in the legend rather than as a second mark: they are one fact about
					// the cloud, and drawing them would double the marks for a number the label already states.
					name: `${cohort.label}${formatStatSummary(cohort.stats) === null ? "" : ` — ${formatStatSummary(cohort.stats)}`}`,
					color: seriesColors[index] as string,
					data: cohort.points.map((point) => ({ repeat: point.repeat, tokensPerSecond: point.tokensPerSecond })),
				}))}
				dataKey={{ x: "repeat", y: "tokensPerSecond" }}
				labels={{
					x: t("pages.benchmarks.charts.repeatAxis", "Repeat"),
					y: t("pages.benchmarks.metrics.speed", "tok/s"),
				}}
				xAxisProps={{ allowDecimals: false }}
				withLegend={true}
				data-testid="benchmark-chart-throughput"
			/>
		</ChartPanel>
	);
}

/**
 * Prefill and decode side by side. Time to first token is a SEPARATE panel rather than a third bar here: it is
 * milliseconds against tokens per second, and two scales on one axis makes every comparison drawn on it meaningless.
 */
function SpeedBars({ runs }: { runs: readonly BenchmarkRunSummary[] }) {
	const { t } = useTranslation();
	const bars = useMemo(() => speedBarSeries(runs), [runs]);
	if (bars.length === 0) {
		return null;
	}
	return (
		<>
			<ChartPanel title={t("pages.benchmarks.charts.speedTitle", "Prefill and decode speed per model")}>
				<BarChart
					h={chartHeight}
					data={[...bars]}
					dataKey="label"
					unit=" tok/s"
					valueFormatter={oneDecimal}
					series={[
						{
							name: "promptTokensPerSecond",
							label: t("pages.benchmarks.metrics.pp", "Prompt processing (pp)"),
							color: seriesColors[0],
						},
						{
							name: "generationTokensPerSecond",
							label: t("pages.benchmarks.metrics.tg", "Generation (tg)"),
							color: seriesColors[1],
						},
					]}
					withLegend={true}
					data-testid="benchmark-chart-speed"
				/>
			</ChartPanel>
			{/* One series, so no legend box: the title names it. */}
			<ChartPanel title={t("pages.benchmarks.charts.ttftTitle", "Time to first token per model")}>
				<BarChart
					h={chartHeight}
					data={[...bars]}
					dataKey="label"
					unit=" ms"
					valueFormatter={(value) => String(Math.round(value))}
					series={[{ name: "ttftMs", label: t("pages.benchmarks.metrics.ttft", "Time to first token"), color: seriesColors[2] }]}
					data-testid="benchmark-chart-ttft"
				/>
			</ChartPanel>
		</>
	);
}

/**
 * One panel per model group: its quants against each other on perplexity, which is the comparison the fidelity axis
 * exists to make. KL divergence gets its own panel when the project measured it — it lives near zero while perplexity
 * lives near seven, and one axis cannot carry both honestly.
 */
function FidelityBars({ runs }: { runs: readonly BenchmarkRunSummary[] }) {
	const { t } = useTranslation();
	const groups = useMemo(() => fidelityBarSeries(runs), [runs]);
	const displayOnly = t("pages.benchmarks.fidelity.displayOnly", "Display only — fidelity never ranks a run.");
	if (groups.length === 0) {
		return null;
	}
	return (
		<>
			{groups.map((group) => (
				<ChartPanel
					key={group.key}
					title={t("pages.benchmarks.charts.perplexityTitle", "Perplexity per quant — {{model}}", { model: group.label })}
					note={displayOnly}
				>
					<BarChart
						h={chartHeight}
						data={[...group.bars]}
						dataKey="quant"
						// Perplexity differences between quants are fractions of a point on a value near seven, so a
						// zero-based axis would render every bar at visually the same height.
						yAxisProps={{ domain: ["auto", "auto"] }}
						valueFormatter={(value) => value.toFixed(4)}
						series={[
							{
								name: "perplexityMean",
								label: t("pages.benchmarks.charts.perplexity", "Perplexity"),
								color: seriesColors[3],
							},
						]}
						data-testid="benchmark-chart-perplexity"
					/>
				</ChartPanel>
			))}
			{groups
				.filter((group) => group.bars.some((bar) => bar.kldMean !== null))
				.map((group) => (
					<ChartPanel
						key={`${group.key}-kld`}
						title={t("pages.benchmarks.charts.kldTitle", "KL divergence per quant — {{model}}", { model: group.label })}
						note={displayOnly}
					>
						<BarChart
							h={chartHeight}
							data={[...group.bars]}
							dataKey="quant"
							valueFormatter={(value) => value.toFixed(4)}
							series={[
								{ name: "kldMean", label: t("pages.benchmarks.charts.kld", "KL divergence"), color: seriesColors[4] },
							]}
							data-testid="benchmark-chart-kld"
						/>
					</ChartPanel>
				))}
		</>
	);
}

/**
 * Quality against the reasoning budget the run was frozen with. Rendered only while the budget actually varies across
 * the runs in hand — with one budget the line is a vertical stack of points that answers nothing.
 */
function ReasoningBudgetLine({ runs }: { runs: readonly BenchmarkRunDetail[] }) {
	const { t } = useTranslation();
	const points = useMemo(() => reasoningBudgetSeries(runs), [runs]);
	if (points.length === 0) {
		return null;
	}
	return (
		<ChartPanel
			title={t("pages.benchmarks.charts.reasoningBudgetTitle", "Quality against the frozen reasoning budget")}
			note={t("pages.benchmarks.charts.reasoningBudgetNote", "Across the runs currently selected for comparison.")}
		>
			<LineChart
				h={chartHeight}
				data={[...points]}
				dataKey="reasoningBudgetTokens"
				yAxisProps={{ domain: [0, 100] }}
				curveType="monotone"
				series={[{ name: "qualityScore", label: t("pages.benchmarks.rank.quality", "Quality"), color: seriesColors[5] }]}
				data-testid="benchmark-chart-reasoning-budget"
			/>
		</ChartPanel>
	);
}

/**
 * The benchmark charts. Lazily mounted so the route's first paint does not pay for the charting library, and every
 * panel hides itself when its data does not exist — a project with one model and no fidelity measurement renders the
 * empty state rather than four axes with nothing on them.
 */
export default function BenchmarkCharts({
	runs,
	selectedRuns,
}: {
	runs: readonly BenchmarkRunSummary[];
	/** The compare selection, whose details carry the frozen reasoning budget the list projection does not. */
	selectedRuns: readonly BenchmarkRunDetail[];
}) {
	const { t } = useTranslation();
	// Every panel draws from the same plottable set, so one check answers "is this section empty" rather than four
	// series being recomputed to find out.
	if (!hasChartableRuns(runs)) {
		return (
			<SectionCard title={t("pages.benchmarks.charts.title", "Charts")} data-testid="benchmark-charts">
				<EmptyState message={t("pages.benchmarks.charts.empty", "No measured runs to chart yet.")} size="sm" />
			</SectionCard>
		);
	}

	return (
		<SectionCard title={t("pages.benchmarks.charts.title", "Charts")} data-testid="benchmark-charts">
			<Stack gap="lg">
				<ThroughputScatter runs={runs} />
				<SpeedBars runs={runs} />
				<FidelityBars runs={runs} />
				<ReasoningBudgetLine runs={selectedRuns} />
			</Stack>
		</SectionCard>
	);
}
