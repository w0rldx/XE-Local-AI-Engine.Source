import { ActionIcon, Checkbox, Group, Menu, Stack, Switch, Table, Text, Tooltip } from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconDots, IconRefresh, IconTrash } from "@tabler/icons-react";
import { Fragment, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { BenchmarkLaunchBadges } from "@/features/benchmarks/components/BenchmarkLaunchBadges";
import { BenchmarkJudgeStateBadge, BenchmarkStatusBadge } from "@/features/benchmarks/components/BenchmarkStatusBadge";
import type { BenchmarkRankCohort, BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import { isRunTerminal } from "@/features/benchmarks/models/BenchmarkModels";
import { groupBenchmarkRunsByModel, rankExclusionAction, sortBenchmarkRuns } from "@/features/benchmarks/models/BenchmarkRanking";

interface BenchmarkRunsTableProps {
	runs: readonly BenchmarkRunSummary[];
	cohort: BenchmarkRankCohort;
	selectedRunIds: readonly string[];
	isActionPending?: boolean;
	onToggleRun: (runId: string) => void;
	onRejudgeRun: (run: BenchmarkRunSummary) => void;
	onDeleteRun: (run: BenchmarkRunSummary) => void;
}

const formatDuration = (durationMs: number | null): string => (durationMs === null ? "—" : `${(durationMs / 1000).toFixed(1)}s`);
const formatTimestamp = (epochMs: number): string => (epochMs > 0 ? new Date(epochMs).toLocaleString() : "—");

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
	onToggleRun: (runId: string) => void;
	onRejudgeRun: (run: BenchmarkRunSummary) => void;
	onDeleteRun: (run: BenchmarkRunSummary) => void;
}

function RunRow({
	run,
	selected,
	isActionPending,
	nested = false,
	expander,
	modelLabel,
	onToggleRun,
	onRejudgeRun,
	onDeleteRun,
}: RunRowProps) {
	const { t } = useTranslation();
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
						{run.primaryModelName}
					</Text>
					<Text size="xs" c="dimmed">
						{modelLabel ??
							t(`pages.benchmarks.origin.${run.primaryModelOrigin ?? "legacy"}`, run.primaryModelOrigin ?? "Legacy / Unknown")}
					</Text>
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
				<Text size="sm">{run.tokensPerSecond?.toFixed(1) ?? "—"}</Text>
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
	isActionPending = false,
	onToggleRun,
	onRejudgeRun,
	onDeleteRun,
}: BenchmarkRunsTableProps) {
	const { t } = useTranslation();
	const [grouped, setGrouped] = useState(false);
	const [expandedKeys, setExpandedKeys] = useState<string[]>([]);
	const ordered = useMemo(() => sortBenchmarkRuns(runs), [runs]);
	const groups = useMemo(() => (grouped ? groupBenchmarkRunsByModel(runs) : []), [grouped, runs]);
	const rowProps = { isActionPending, onToggleRun, onRejudgeRun, onDeleteRun };

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
			<Table.ScrollContainer minWidth={1200}>
				<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="benchmark-runs-table">
					<Table.Thead>
						<Table.Tr>
							<Table.Th>{t("pages.benchmarks.rank.compare", "Compare")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.rank", "Rank")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.model", "Model")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.quality", "Quality")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.judgeScore", "Judge")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.userScore", "Operator")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.metrics.speed", "tok/s")}</Table.Th>
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
																nested={true}
															/>
														))
												: null}
										</Fragment>
									);
								})
							: ordered.map((run) => (
									<RunRow {...rowProps} key={run.id} run={run} selected={selectedRunIds.includes(run.id)} />
								))}
					</Table.Tbody>
				</Table>
			</Table.ScrollContainer>
		</Stack>
	);
}
