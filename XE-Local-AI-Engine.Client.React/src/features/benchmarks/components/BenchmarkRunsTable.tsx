import { ActionIcon, Button, Checkbox, Group, Menu, Stack, Switch, Table, Text, Tooltip } from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconDots, IconRefresh, IconRuler2, IconTrash } from "@tabler/icons-react";
import { Fragment, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
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
import type { BenchmarkRankCohort, BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkBaseModelLabel,
	benchmarkQuantTag,
	isBenchmarkRunIncomplete,
	isBenchmarkRunReasoningExhausted,
	isBenchmarkRunTruncated,
	isRunTerminal,
} from "@/features/benchmarks/models/BenchmarkModels";
import { groupBenchmarkRunsByModel, rankExclusionAction, sortBenchmarkRuns } from "@/features/benchmarks/models/BenchmarkRanking";
import type { BenchmarkRepeatStats } from "@/features/benchmarks/models/BenchmarkThroughput";
import {
	benchmarkRepeatCohortKey,
	benchmarkRepeatStats,
	formatLatencyMs,
	formatStatSummary,
	formatTokensPerSecond,
} from "@/features/benchmarks/models/BenchmarkThroughput";

interface BenchmarkRunsTableProps {
	runs: readonly BenchmarkRunSummary[];
	cohort: BenchmarkRankCohort;
	selectedRunIds: readonly string[];
	/** Runs the project has in total. More than `runs.length` means the table shows one page of them. */
	totalCount?: number;
	isLoadingMore?: boolean;
	onLoadMore?: () => void;
	isActionPending?: boolean;
	onToggleRun: (runId: string) => void;
	onRejudgeRun: (run: BenchmarkRunSummary) => void;
	onMeasureFidelity: (run: BenchmarkRunSummary) => void;
	onDeleteRun: (run: BenchmarkRunSummary) => void;
}

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
			? t("pages.benchmarks.metrics.cachedTooltip", "{{tokens}} prompt tokens came from the KV cache, so the prompt speed is not a cold prefill.", {
					tokens: cachedPromptTokens,
				})
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
			: t("pages.benchmarks.fidelity.pplTooltip", "Perplexity {{value}} over {{chunks}} chunks at a {{window}}-token window ({{corpus}})", {
					value: perplexity,
					chunks: fidelity.perplexityChunks ?? "—",
					window: fidelity.perplexityContextTokens ?? "—",
					corpus: fidelity.perplexityCorpusId ?? "—",
				}),
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

function QualityScoreCell({ run }: { run: BenchmarkRunSummary }) {
	const { t } = useTranslation();
	if (run.qualityScore === null) {
		return <Text size="sm">—</Text>;
	}
	return (
		<Group gap={6} wrap="nowrap">
			<Text size="sm" fw={700}>
				{run.qualityScore}
			</Text>
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

interface RunRowProps {
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
	onToggleRun: (runId: string) => void;
	onRejudgeRun: (run: BenchmarkRunSummary) => void;
	onMeasureFidelity: (run: BenchmarkRunSummary) => void;
	onDeleteRun: (run: BenchmarkRunSummary) => void;
}

function RunRow({
	run,
	selected,
	isActionPending,
	nested = false,
	expander,
	modelLabel,
	modelName,
	stats,
	onToggleRun,
	onRejudgeRun,
	onMeasureFidelity,
	onDeleteRun,
}: RunRowProps) {
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
						{quant ? (
							<StatusBadge color="gray" label={quant} data-testid={`benchmark-run-quant-${run.id}`} />
						) : null}
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
				<QualityScoreCell run={run} />
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

/**
 * Every run of one project, ranked. Unranked rows are kept and explained rather than hidden, the cohort line states
 * what the ranking was computed against, and "group by model" folds a model's history under its best-ranked run —
 * all client-side over the loaded page.
 */
export function BenchmarkRunsTable({
	runs,
	cohort,
	selectedRunIds,
	totalCount,
	isLoadingMore = false,
	onLoadMore,
	isActionPending = false,
	onToggleRun,
	onRejudgeRun,
	onMeasureFidelity,
	onDeleteRun,
}: BenchmarkRunsTableProps) {
	const { t } = useTranslation();
	const [grouped, setGrouped] = useState(false);
	const [expandedKeys, setExpandedKeys] = useState<string[]>([]);
	const ordered = useMemo(() => sortBenchmarkRuns(runs), [runs]);
	const groups = useMemo(() => (grouped ? groupBenchmarkRunsByModel(runs) : []), [grouped, runs]);
	// Computed over EVERY run of the project, not per group: a cohort is (model, KV, launch identity), which is
	// narrower than a group and never wider, so scoping it to the rendered group would only recompute the same thing.
	const stats = useMemo(() => benchmarkRepeatStats(runs), [runs]);
	const statsFor = (run: BenchmarkRunSummary): BenchmarkRepeatStats | undefined => stats.get(benchmarkRepeatCohortKey(run));
	const rowProps = { isActionPending, onToggleRun, onRejudgeRun, onMeasureFidelity, onDeleteRun };

	if (runs.length === 0) {
		return <EmptyState message={t("pages.benchmarks.rank.empty", "No runs yet. Start one to populate the ranking.")} size="sm" />;
	}

	return (
		<Stack gap="sm">
			<Group justify="space-between" align="center">
				<Text size="sm" c="dimmed" data-testid="benchmark-rank-cohort">
					{t("pages.benchmarks.rank.cohort", "{{ranked}} of {{scored}} ranked", {
						ranked: cohort.rankedCount,
						scored: cohort.totalScored,
					})}
					{cohort.policyRevision === null
						? ""
						: ` · ${t("pages.benchmarks.rank.cohortPolicy", "judge policy r{{revision}}", {
								revision: cohort.policyRevision,
							})}`}
					{cohort.cohortGeneration === null
						? ""
						: ` · ${t("pages.benchmarks.rank.cohortGeneration", "gen {{generation}}", {
								generation: cohort.cohortGeneration,
							})}`}
				</Text>
				<Switch
					size="sm"
					checked={grouped}
					label={t("pages.benchmarks.rank.groupByModel", "Group by model")}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						setGrouped(checked);
					}}
					data-testid="benchmark-group-by-model"
				/>
			</Group>
			<Table.ScrollContainer minWidth={1360}>
				<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="benchmark-runs-table">
					<Table.Thead>
						<Table.Tr>
							<Table.Th>{t("pages.benchmarks.rank.compare", "Compare")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.rank", "Rank")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.model", "Model")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.quality", "Quality")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.judgeScore", "Judge")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.userScore", "Operator")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.metrics.speedColumn", "tok/s (tg)")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.fidelity.column", "PPL / KLD")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.metrics.duration", "Duration")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.launch", "KV / context")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.created", "Created")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.status", "Status")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.actions", "Actions")}</Table.Th>
						</Table.Tr>
					</Table.Thead>
					<Table.Tbody>
						{grouped
							? groups.map((group) => {
									const expanded = expandedKeys.includes(group.key);
									return (
										<Fragment key={group.key}>
											<RunRow
												{...rowProps}
												run={group.leader}
												selected={selectedRunIds.includes(group.leader.id)}
												stats={statsFor(group.leader)}
												modelName={benchmarkBaseModelLabel(group.leader.primaryModelName)}
												modelLabel={t("pages.benchmarks.rank.groupCount", "{{count}} runs of this model", {
													count: group.runs.length,
												})}
												expander={
													<ActionIcon
														variant="subtle"
														size="sm"
														aria-label={t("pages.benchmarks.rank.expandGroup", "Show this model's other runs")}
														aria-expanded={expanded}
														onClick={() =>
															setExpandedKeys((current) =>
																current.includes(group.key)
																	? current.filter((key) => key !== group.key)
																	: [...current, group.key],
															)
														}
														data-testid={`benchmark-group-toggle-${group.key}`}
													>
														{expanded ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
													</ActionIcon>
												}
											/>
											{expanded
												? group.runs
														.slice(1)
														.map((run) => (
															<RunRow
																{...rowProps}
																key={run.id}
																run={run}
																selected={selectedRunIds.includes(run.id)}
																stats={statsFor(run)}
																nested={true}
															/>
														))
												: null}
										</Fragment>
									);
								})
							: ordered.map((run) => (
									<RunRow {...rowProps} key={run.id} run={run} selected={selectedRunIds.includes(run.id)} stats={statsFor(run)} />
								))}
					</Table.Tbody>
				</Table>
			</Table.ScrollContainer>
			{/* A batch launch can make more runs than one page holds. Saying so — and how many are missing — is the only
			    thing that keeps the ranking honest when the table shows a prefix of it. */}
			{totalCount !== undefined && totalCount > runs.length ? (
				<Group gap="sm" justify="center">
					<Text size="sm" c="dimmed" data-testid="benchmark-runs-loaded">
						{t("pages.benchmarks.rank.loaded", "Showing {{loaded}} of {{total}} runs", {
							loaded: runs.length,
							total: totalCount,
						})}
					</Text>
					<Button
						variant="subtle"
						size="xs"
						loading={isLoadingMore}
						onClick={onLoadMore}
						data-testid="benchmark-runs-load-more"
					>
						{t("pages.benchmarks.rank.loadMore", "Load more")}
					</Button>
				</Group>
			) : null}
		</Stack>
	);
}
