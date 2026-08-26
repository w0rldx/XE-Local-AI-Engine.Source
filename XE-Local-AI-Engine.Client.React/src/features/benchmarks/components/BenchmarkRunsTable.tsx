import { ActionIcon, Button, Group, Stack, Switch, Table, Text } from "@mantine/core";
import { IconChevronDown, IconChevronRight } from "@tabler/icons-react";
import { Fragment, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { BenchmarkRunRow } from "@/features/benchmarks/components/BenchmarkRunRow";
import type {
	BenchmarkPairwiseRunScore,
	BenchmarkRankCohort,
	BenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkBaseModelLabel } from "@/features/benchmarks/models/BenchmarkModels";
import { groupBenchmarkRunsByModel, sortBenchmarkRuns } from "@/features/benchmarks/models/BenchmarkRanking";
import type { BenchmarkRepeatStats } from "@/features/benchmarks/models/BenchmarkThroughput";
import { benchmarkRepeatCohortKey, benchmarkRepeatStats } from "@/features/benchmarks/models/BenchmarkThroughput";

interface BenchmarkRunsTableProps {
	runs: readonly BenchmarkRunSummary[];
	/** Bootstrap intervals by run id, when the project judges pairwise. The score itself rides `qualityScore`. */
	pairwiseScores?: ReadonlyMap<string, BenchmarkPairwiseRunScore>;
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

/**
 * Every run of one project, ranked. Unranked rows are kept and explained rather than hidden, the cohort line states
 * what the ranking was computed against, and "group by model" folds a model's history under its best-ranked run —
 * all client-side over the loaded page.
 */
export function BenchmarkRunsTable({
	runs,
	pairwiseScores,
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
	const expandedKeySet = useMemo(() => new Set(expandedKeys), [expandedKeys]);
	const selectedRunIdSet = useMemo(() => new Set(selectedRunIds), [selectedRunIds]);
	const ordered = useMemo(() => sortBenchmarkRuns(runs), [runs]);
	const groups = useMemo(() => (grouped ? groupBenchmarkRunsByModel(runs) : []), [grouped, runs]);
	// Computed over EVERY run of the project, not per group: a cohort is (model, KV, launch identity), which is
	// narrower than a group and never wider, so scoping it to the rendered group would only recompute the same thing.
	const stats = useMemo(() => benchmarkRepeatStats(runs), [runs]);
	const statsFor = (run: BenchmarkRunSummary): BenchmarkRepeatStats | undefined => stats.get(benchmarkRepeatCohortKey(run));
	const pairwiseFor = (run: BenchmarkRunSummary): BenchmarkPairwiseRunScore | undefined => pairwiseScores?.get(run.id);
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
									const expanded = expandedKeySet.has(group.key);
									return (
										<Fragment key={group.key}>
											<BenchmarkRunRow
												{...rowProps}
												run={group.leader}
												selected={selectedRunIdSet.has(group.leader.id)}
												stats={statsFor(group.leader)}
												pairwise={pairwiseFor(group.leader)}
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
															setExpandedKeys((current) => {
																const next = new Set(current);
																if (next.has(group.key)) {
																	next.delete(group.key);
																} else {
																	next.add(group.key);
																}
																return [...next];
															})
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
															<BenchmarkRunRow
																{...rowProps}
																key={run.id}
																run={run}
																selected={selectedRunIdSet.has(run.id)}
																stats={statsFor(run)}
																pairwise={pairwiseFor(run)}
																nested={true}
															/>
														))
												: null}
										</Fragment>
									);
								})
							: ordered.map((run) => (
									<BenchmarkRunRow
										{...rowProps}
										key={run.id}
										run={run}
										selected={selectedRunIdSet.has(run.id)}
										stats={statsFor(run)}
										pairwise={pairwiseFor(run)}
									/>
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
					<Button variant="subtle" size="xs" loading={isLoadingMore} onClick={onLoadMore} data-testid="benchmark-runs-load-more">
						{t("pages.benchmarks.rank.loadMore", "Load more")}
					</Button>
				</Group>
			) : null}
		</Stack>
	);
}
