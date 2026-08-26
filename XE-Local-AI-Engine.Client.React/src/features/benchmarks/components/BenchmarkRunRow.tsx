import { ActionIcon, Checkbox, Group, Menu, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconDots, IconRefresh, IconRuler2, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { BenchmarkLaunchBadges } from "@/features/benchmarks/components/BenchmarkLaunchBadges";
import {
	BenchmarkIncompleteBadge,
	BenchmarkJudgeStateBadge,
	BenchmarkReasoningExhaustedBadge,
	BenchmarkStatusBadge,
	BenchmarkTruncatedBadge,
} from "@/features/benchmarks/components/BenchmarkStatusBadge";
import {
	canMeasureFidelity,
	formatKldValue,
	formatPerplexity,
	formatTopTokenAgreement,
	isKldComparable,
} from "@/features/benchmarks/models/BenchmarkFidelity";
import type { BenchmarkPairwiseRunScore, BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkQuantTag,
	isBenchmarkRunIncomplete,
	isBenchmarkRunReasoningExhausted,
	isBenchmarkRunTruncated,
	isRunTerminal,
} from "@/features/benchmarks/models/BenchmarkModels";
import { rankExclusionAction } from "@/features/benchmarks/models/BenchmarkRanking";
import type { BenchmarkRepeatStats } from "@/features/benchmarks/models/BenchmarkThroughput";
import { formatLatencyMs, formatStatSummary, formatTokensPerSecond } from "@/features/benchmarks/models/BenchmarkThroughput";

const formatDuration = (durationMs: number | null): string => (durationMs === null ? "—" : `${(durationMs / 1000).toFixed(1)}s`);
const formatTimestamp = (epochMs: number): string => (epochMs > 0 ? new Date(epochMs).toLocaleString() : "—");

// tg (decode) leads because it is the number an operator compares models by; pp and TTFT ride under it because a fast
// decode over a slow prefill is a different machine from a fast one, and the blended figure this replaced hid that.
// Exact values live in the tooltip so the column stays narrow without rounding away the measurement.
function ThroughputCell({ run, stats }: { run: BenchmarkRunSummary; stats?: BenchmarkRepeatStats }) {
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

/**
 * How far this build drifted from the weights it was made from. Perplexity leads because it needs no second model;
 * KLD rides under it when the project opted in. Display only, and the copy never calls a number good or bad — a lower
 * perplexity is not a better answer, which is exactly why neither figure ranks anything.
 *
 * A KLD measured against something the project no longer expects renders `kld-stale` — a BADGE, never a greyed number.
 * A figure a reader can still see is a figure they will compare, and one taken over a different corpus, chunk count or
 * base model means something different from the one beside it.
 */
function FidelityCell({ run }: { run: BenchmarkRunSummary }) {
	const { t } = useTranslation();
	const fidelity = run.fidelity;
	if (fidelity === null || fidelity.status === "skipped") {
		return <Text size="sm">—</Text>;
	}
	if (fidelity.status === "queued" || fidelity.status === "running") {
		return (
			<StatusBadge
				color="blue"
				inProgress={true}
				label={t(`pages.benchmarks.fidelity.status.${fidelity.status}`, fidelity.status)}
				data-testid={`benchmark-fidelity-status-${run.id}`}
			/>
		);
	}
	if (fidelity.status === "failed" || fidelity.status === "cancelled") {
		return (
			<Tooltip
				label={fidelity.errorMessage ?? t("pages.benchmarks.fidelity.noReason", "The node recorded no reason.")}
				multiline={true}
				w={280}
			>
				<span>
					<StatusBadge
						color={fidelity.status === "failed" ? "red" : "gray"}
						label={t(`pages.benchmarks.fidelity.status.${fidelity.status}`, fidelity.status)}
						data-testid={`benchmark-fidelity-status-${run.id}`}
					/>
				</span>
			</Tooltip>
		);
	}
	const perplexity = formatPerplexity(fidelity);
	const comparable = isKldComparable(fidelity);
	const tooltip = [
		perplexity === null
			? null
			: t(
					"pages.benchmarks.fidelity.pplTooltip",
					"Perplexity {{value}} over {{chunks}} chunks at a {{window}}-token window ({{corpus}})",
					{
						value: perplexity,
						chunks: fidelity.perplexityChunks ?? "—",
						window: fidelity.perplexityContextTokens ?? "—",
						corpus: fidelity.perplexityCorpusId ?? "—",
					},
				),
		comparable && fidelity.kldMean !== null
			? t("pages.benchmarks.fidelity.kldTooltip", "KL divergence mean {{mean}}, p99 {{p99}}, top-token agreement {{agreement}}", {
					mean: formatKldValue(fidelity.kldMean) ?? "—",
					p99: formatKldValue(fidelity.kldP99) ?? "—",
					agreement: formatTopTokenAgreement(fidelity.topTokenAgreement) ?? "—",
				})
			: null,
		t("pages.benchmarks.fidelity.displayOnly", "Display only — fidelity never ranks a run."),
	]
		.filter((line): line is string => line !== null)
		.join("\n");
	return (
		<Tooltip label={tooltip} multiline={true} w={320}>
			<Stack gap={0} data-testid={`benchmark-fidelity-${run.id}`}>
				<Text size="sm">{perplexity ?? "—"}</Text>
				{fidelity.kldState === "kld-stale" ? (
					<StatusBadge
						color="orange"
						label={t("pages.benchmarks.fidelity.kldStale", "kld-stale")}
						data-testid={`benchmark-fidelity-kld-stale-${run.id}`}
					/>
				) : comparable && fidelity.kldMean !== null ? (
					<Text size="xs" c="dimmed" data-testid={`benchmark-fidelity-kld-${run.id}`}>
						{t("pages.benchmarks.fidelity.kldLine", "KLD {{mean}} · p99 {{p99}} · {{agreement}}", {
							mean: formatKldValue(fidelity.kldMean),
							p99: formatKldValue(fidelity.kldP99) ?? "—",
							agreement: formatTopTokenAgreement(fidelity.topTokenAgreement) ?? "—",
						})}
					</Text>
				) : null}
			</Stack>
		</Tooltip>
	);
}

function QualityScoreCell({ run, pairwise }: { run: BenchmarkRunSummary; pairwise?: BenchmarkPairwiseRunScore }) {
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

// A missing rank is never left bare: the node says WHY the run is out of the cohort, and the chip carries that reason
// plus the action that would bring it back in.
function RankCell({ run }: { run: BenchmarkRunSummary }) {
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

export interface BenchmarkRunRowProps {
	run: BenchmarkRunSummary;
	selected: boolean;
	isActionPending: boolean;
	/** Grouped child rows are indented under their model's row and carry no expander of their own. */
	nested?: boolean;
	expander?: React.ReactNode;
	modelLabel?: React.ReactNode;
	/** A group leader shows the BASE model; every other row shows the exact model name it ran. */
	modelName?: string;
	stats?: BenchmarkRepeatStats;
	pairwise?: BenchmarkPairwiseRunScore;
	onToggleRun: (runId: string) => void;
	onRejudgeRun: (run: BenchmarkRunSummary) => void;
	onMeasureFidelity: (run: BenchmarkRunSummary) => void;
	onDeleteRun: (run: BenchmarkRunSummary) => void;
}

export function BenchmarkRunRow({
	run,
	selected,
	isActionPending,
	nested = false,
	expander,
	modelLabel,
	modelName,
	stats,
	pairwise,
	onToggleRun,
	onRejudgeRun,
	onMeasureFidelity,
	onDeleteRun,
}: BenchmarkRunRowProps) {
	const { t } = useTranslation();
	const quant = benchmarkQuantTag(run.primaryModelName);
	return (
		<Table.Tr data-testid={`benchmark-run-row-${run.id}`}>
			<Table.Td>
				<Group gap={4} wrap="nowrap">
					{expander}
					<Checkbox
						checked={selected}
						aria-label={t("pages.benchmarks.run.select", "Show {{model}} in the detail view", {
							model: run.primaryModelName,
						})}
						onChange={() => onToggleRun(run.id)}
						data-testid={`benchmark-run-select-${run.id}`}
					/>
				</Group>
			</Table.Td>
			<Table.Td>
				<RankCell run={run} />
			</Table.Td>
			<Table.Td pl={nested ? "xl" : undefined}>
				<Stack gap={2} style={{ minWidth: 0 }}>
					<Text size="sm" fw={500} truncate="end">
						{modelName ?? run.primaryModelName}
					</Text>
					<Group gap={4} wrap="nowrap">
						{/* The quant is what tells a group's rows apart once the header carries the base model — without it a
						    grouped model's three quants would render as three identical-looking rows. */}
						{quant ? <StatusBadge color="gray" label={quant} data-testid={`benchmark-run-quant-${run.id}`} /> : null}
						{run.repeatIndex === null ? null : (
							<StatusBadge
								color={run.isWarmup ? "orange" : "gray"}
								label={
									run.isWarmup
										? t("pages.benchmarks.rank.warmupBadge", "warm-up")
										: t("pages.benchmarks.rank.repeatBadge", "#{{index}}", { index: run.repeatIndex })
								}
								data-testid={`benchmark-run-repeat-${run.id}`}
							/>
						)}
						<Text size="xs" c="dimmed" truncate="end">
							{modelLabel ??
								t(`pages.benchmarks.origin.${run.primaryModelOrigin ?? "legacy"}`, run.primaryModelOrigin ?? "Legacy / Unknown")}
						</Text>
					</Group>
				</Stack>
			</Table.Td>
			<Table.Td>
				<QualityScoreCell run={run} pairwise={pairwise} />
			</Table.Td>
			<Table.Td>
				<Text size="sm">{run.judge.score ?? "—"}</Text>
			</Table.Td>
			<Table.Td>
				<Text size="sm">{run.userScore ?? "—"}</Text>
			</Table.Td>
			<Table.Td>
				<ThroughputCell run={run} stats={stats} />
			</Table.Td>
			<Table.Td>
				<FidelityCell run={run} />
			</Table.Td>
			<Table.Td>
				<Text size="sm">{formatDuration(run.durationMs)}</Text>
			</Table.Td>
			<Table.Td>
				<Stack gap={2}>
					<BenchmarkLaunchBadges launch={run.primaryLaunch} data-testid={`benchmark-run-launch-${run.id}`} />
					<Text size="xs" c="dimmed">
						{t("pages.benchmarks.rank.context", "ctx {{tokens}}", {
							tokens: run.effectiveContextTokens ?? run.requestedContextTokens,
						})}
					</Text>
				</Stack>
			</Table.Td>
			<Table.Td>
				<Text size="xs">{formatTimestamp(run.createdAtUtc)}</Text>
			</Table.Td>
			<Table.Td>
				<Group gap={4} wrap="nowrap">
					<BenchmarkStatusBadge status={run.primaryStatus} />
					{/* Reasoning exhaustion IS truncation, so it replaces the generic badge rather than adding a second
					    one — two badges saying "cut off" would not tell the operator which budget to raise. */}
					{isBenchmarkRunReasoningExhausted(run) ? (
						<BenchmarkReasoningExhaustedBadge testId={`benchmark-reasoning-exhausted-${run.id}`} />
					) : isBenchmarkRunTruncated(run) ? (
						<BenchmarkTruncatedBadge testId={`benchmark-truncated-${run.id}`} />
					) : null}
					{isBenchmarkRunIncomplete(run) ? <BenchmarkIncompleteBadge testId={`benchmark-incomplete-${run.id}`} /> : null}
					<BenchmarkJudgeStateBadge state={run.judge.state} />
				</Group>
			</Table.Td>
			<Table.Td>
				<Menu position="bottom-end" withinPortal={true}>
					<Menu.Target>
						<ActionIcon
							variant="subtle"
							aria-label={t("pages.benchmarks.run.actions", "Run actions")}
							data-testid={`benchmark-run-actions-${run.id}`}
						>
							<IconDots size={16} />
						</ActionIcon>
					</Menu.Target>
					<Menu.Dropdown>
						<Menu.Item
							leftSection={<IconRefresh size={14} />}
							disabled={isActionPending || run.primaryStatus !== "Succeeded"}
							onClick={() => onRejudgeRun(run)}
						>
							{t("pages.benchmarks.judge.rejudge", "Re-judge run")}
						</Menu.Item>
						{/* A re-measure inserts a new immutable attempt, so the previous numbers survive one that fails —
						    which is what makes this safe to offer without a confirmation. */}
						<Menu.Item
							leftSection={<IconRuler2 size={14} />}
							disabled={isActionPending || !canMeasureFidelity(run)}
							onClick={() => onMeasureFidelity(run)}
							data-testid={`benchmark-measure-fidelity-${run.id}`}
						>
							{t("pages.benchmarks.fidelity.measure", "Measure fidelity")}
						</Menu.Item>
						<Menu.Item
							color="red"
							leftSection={<IconTrash size={14} />}
							disabled={isActionPending || !isRunTerminal(run)}
							onClick={() => onDeleteRun(run)}
						>
							{t("pages.benchmarks.run.delete", "Delete terminal run")}
						</Menu.Item>
					</Menu.Dropdown>
				</Menu>
			</Table.Td>
		</Table.Tr>
	);
}
