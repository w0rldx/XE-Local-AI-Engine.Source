import { isKldComparable } from "@/features/benchmarks/models/BenchmarkFidelity";
import type { BenchmarkRunDetail, BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkBaseModelLabel, benchmarkQuantTag } from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkStatSummary } from "@/features/benchmarks/models/BenchmarkThroughput";
import { benchmarkRepeatCohortKey, benchmarkRepeatStats } from "@/features/benchmarks/models/BenchmarkThroughput";

// Pure shaping for the charts. Kept out of the chart component so the rules that decide WHICH runs a point may be
// computed from are testable without rendering anything — and so a chart library swap costs no arithmetic.
//
// One rule runs through all of it: a warm-up and a non-succeeded run are excluded everywhere. A warm-up is the
// first-launch cost the repeats after it were meant not to pay, so plotting it would show the spread of the thing
// being controlled for; a failed run has no measurement at all, and 0 is a measurement.

const isPlottable = (run: BenchmarkRunSummary): boolean => run.primaryStatus === "Succeeded" && !run.isWarmup;
const finite = (value: number | null | undefined): value is number => value !== null && value !== undefined && Number.isFinite(value);

/**
 * Whether anything at all can be charted. Every panel derives from the same set of plottable runs, so one check
 * answers "is this section empty" without recomputing four series to find out.
 */
export const hasChartableRuns = (runs: readonly BenchmarkRunSummary[]): boolean => runs.some(isPlottable);

/** The row label for a run: the base model with its quant tag, which is what distinguishes a group's rows. */
export function benchmarkRunLabel(run: BenchmarkRunSummary): string {
	const quant = benchmarkQuantTag(run.primaryModelName);
	return quant === "" ? run.primaryModelName : `${benchmarkBaseModelLabel(run.primaryModelName)} ${quant}`;
}

/** One measured repeat, as a scatter point. `repeat` is the node's own index, so a group reads left to right in order. */
export interface BenchmarkThroughputPoint {
	runId: string;
	label: string;
	repeat: number;
	tokensPerSecond: number;
}

/**
 * One repeat cohort — the same model build, KV type, launch identity and repeat mode — with its measured points and
 * the mean/σ over them. Cohorts rather than models, because two runs of one model on different launch arguments are
 * two different experiments and a spread computed across them would really be a configuration difference.
 */
export interface BenchmarkThroughputSeries {
	key: string;
	label: string;
	points: BenchmarkThroughputPoint[];
	/**
	 * Mean and sample deviation over the cohort's points, or null when it reported none. A one-sample summary is kept
	 * rather than nulled — the caller renders it through `formatStatSummary`, which is where "± 0 (n=1)" is refused,
	 * because that is a display rule and this is the measurement.
	 */
	stats: BenchmarkStatSummary | null;
}

export function throughputScatterSeries(runs: readonly BenchmarkRunSummary[]): BenchmarkThroughputSeries[] {
	const stats = benchmarkRepeatStats(runs);
	const cohorts = new Map<string, BenchmarkThroughputSeries>();
	for (const run of runs) {
		if (!isPlottable(run) || !finite(run.tokensPerSecond)) {
			continue;
		}
		const key = benchmarkRepeatCohortKey(run);
		const series = cohorts.get(key) ?? {
			key,
			label: benchmarkRunLabel(run),
			points: [],
			stats: stats.get(key)?.tokensPerSecond ?? null,
		};
		series.points.push({
			runId: run.id,
			label: series.label,
			// A plain run carries no repeat index; it is the cohort's only point, so 1 places it where a single repeat would be.
			repeat: run.repeatIndex ?? 1,
			tokensPerSecond: run.tokensPerSecond,
		});
		cohorts.set(key, series);
	}
	return [...cohorts.values()].map((series) => ({
		...series,
		points: [...series.points].sort((left, right) => left.repeat - right.repeat),
	}));
}

/** Prefill, decode and latency of one model side by side. Means over its plottable runs; null where none reported it. */
export interface BenchmarkSpeedBar {
	label: string;
	promptTokensPerSecond: number | null;
	generationTokensPerSecond: number | null;
	ttftMs: number | null;
}

const mean = (values: readonly number[]): number | null =>
	values.length === 0 ? null : values.reduce((total, value) => total + value, 0) / values.length;

/**
 * pp, tg and TTFT per model build. Grouped by the exact build rather than the base model: a chart that averaged a
 * model's Q3 and Q8 into one bar would report a speed neither quant has.
 */
export function speedBarSeries(runs: readonly BenchmarkRunSummary[]): BenchmarkSpeedBar[] {
	const builds = new Map<string, BenchmarkRunSummary[]>();
	for (const run of runs) {
		if (!isPlottable(run)) {
			continue;
		}
		const existing = builds.get(run.primaryModelName);
		if (existing) {
			existing.push(run);
		} else {
			builds.set(run.primaryModelName, [run]);
		}
	}
	return [...builds.values()].map((cohort) => ({
		label: benchmarkRunLabel(cohort[0] as BenchmarkRunSummary),
		promptTokensPerSecond: mean(cohort.map((run) => run.throughput.promptTokensPerSecond).filter(finite)),
		generationTokensPerSecond: mean(cohort.map((run) => run.tokensPerSecond).filter(finite)),
		ttftMs: mean(cohort.map((run) => run.throughput.ttftMs).filter(finite)),
	}));
}

/** One quant's fidelity bar inside its model group. `kldMean` is null unless the node called the measurement comparable. */
export interface BenchmarkFidelityBar {
	label: string;
	quant: string;
	perplexityMean: number;
	perplexityStdErr: number | null;
	kldMean: number | null;
}

/** A model group's quants, keyed by the base model so the chart can render one panel per model. */
export interface BenchmarkFidelityGroup {
	key: string;
	label: string;
	bars: BenchmarkFidelityBar[];
}

/**
 * Perplexity per quant, grouped by base model — the comparison the axis exists for. Two rules it enforces:
 *
 * - Only runs whose perplexity was measured over the SAME corpus at the SAME window enter one group. Perplexity is
 *   comparable at a fixed window and nowhere else, so a run measured differently is dropped rather than plotted beside
 *   numbers it does not compare with.
 * - A KLD is plotted only while the node calls it comparable; a stale one contributes null, never a bar.
 *
 * A group with one quant is dropped: a single bar answers no comparison, and the panel it would occupy is noise.
 */
export function fidelityBarSeries(runs: readonly BenchmarkRunSummary[]): BenchmarkFidelityGroup[] {
	const groups = new Map<string, Map<string, BenchmarkFidelityBar>>();
	for (const run of runs) {
		const fidelity = run.fidelity;
		if (!isPlottable(run) || fidelity === null || !finite(fidelity.perplexityMean)) {
			continue;
		}
		// The corpus and window are what make two perplexity numbers the same measurement; without both, they are two
		// different questions plotted on one axis.
		const comparabilityKey = `${run.modelGroupKey}|${fidelity.perplexityCorpusId ?? ""}|${fidelity.perplexityContextTokens ?? ""}`;
		const bars = groups.get(comparabilityKey) ?? new Map<string, BenchmarkFidelityBar>();
		const quant = benchmarkQuantTag(run.primaryModelName);
		// One bar per quant: repeats of one quant measure the same weights, so the first measurement stands for them.
		if (!bars.has(quant)) {
			bars.set(quant, {
				label: benchmarkRunLabel(run),
				quant: quant === "" ? run.primaryModelName : quant,
				perplexityMean: fidelity.perplexityMean,
				perplexityStdErr: fidelity.perplexityStdErr,
				kldMean: isKldComparable(fidelity) ? fidelity.kldMean : null,
			});
		}
		groups.set(comparabilityKey, bars);
	}
	return [...groups.entries()]
		.map(([key, bars]) => ({
			key,
			label: benchmarkBaseModelLabel([...bars.values()][0]?.label ?? key),
			bars: [...bars.values()],
		}))
		.filter((group) => group.bars.length > 1);
}

/** One run's quality against the reasoning budget it was frozen with. */
export interface BenchmarkReasoningBudgetPoint {
	runId: string;
	label: string;
	reasoningBudgetTokens: number;
	qualityScore: number;
}

/**
 * Quality against the frozen reasoning budget, ascending. Empty unless the budget actually VARIES across the runs in
 * hand: with one budget the chart is a vertical stack of points that says nothing, and a project can only carry a
 * second budget after being unfrozen, so hiding it is the ordinary case rather than a degraded one.
 *
 * Takes details rather than summaries because `reasoningBudgetTokens` is a detail-only member — the list projection
 * does not carry it.
 */
export function reasoningBudgetSeries(runs: readonly BenchmarkRunDetail[]): BenchmarkReasoningBudgetPoint[] {
	const points = runs
		.filter((run) => isPlottable(run) && finite(run.reasoningBudgetTokens) && finite(run.qualityScore))
		.map((run) => ({
			runId: run.id,
			label: benchmarkRunLabel(run),
			reasoningBudgetTokens: run.reasoningBudgetTokens as number,
			qualityScore: run.qualityScore as number,
		}))
		.sort((left, right) => left.reasoningBudgetTokens - right.reasoningBudgetTokens);
	return new Set(points.map((point) => point.reasoningBudgetTokens)).size > 1 ? points : [];
}
