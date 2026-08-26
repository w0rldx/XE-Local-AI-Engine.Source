import { Button, Group, Loader, SegmentedControl, SimpleGrid, Text } from "@mantine/core";
import { IconChartHistogram } from "@tabler/icons-react";
import { lazy, Suspense } from "react";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { BenchmarkCellsTable } from "@/features/benchmarks/components/BenchmarkCellsTable";
import { BenchmarkLaunchCompare } from "@/features/benchmarks/components/BenchmarkLaunchCompare";
import { BenchmarkPairedDelta } from "@/features/benchmarks/components/BenchmarkPairedDelta";
import { BenchmarkPairwiseMatrix } from "@/features/benchmarks/components/BenchmarkPairwiseMatrix";
import { BenchmarkRunLivePane } from "@/features/benchmarks/components/BenchmarkRunLivePane";
import { BenchmarkRunsTable } from "@/features/benchmarks/components/BenchmarkRunsTable";
import type { BenchmarksPageController } from "@/features/benchmarks/hooks/useBenchmarksPageController";
import { canComparePairedDeltas } from "@/features/benchmarks/models/BenchmarkCells";

const BenchmarkCharts = lazy(() => import("@/features/benchmarks/components/BenchmarkCharts"));

export function BenchmarkRunResults({ controller }: { readonly controller: BenchmarksPageController }) {
	const {
		t,
		runsQuery,
		runs,
		pairwiseScores,
		selectedRunIds,
		rejudgeRun,
		deleteRun,
		measureFidelity,
		toggleRun,
		rejudgeOne,
		measureRunFidelity,
		removeRun,
		detail,
		chartsOpen,
		setChartsOpen,
		selectedRunDetails,
		isSuite,
		ranking,
		setRanking,
		showCells,
		cellsQuery,
		taskItemsQuery,
		leafItemCount,
		startRun,
		rerunCell,
	} = controller;
	return (
		<>
			{runsQuery.data && runs.length > 0 ? (
				<SectionCard title={t("pages.benchmarks.runs", "Runs")}>
					{isSuite ? (
						<SegmentedControl
							size="xs"
							value={ranking}
							onChange={(value) => setRanking(value === "runs" ? "runs" : "cells")}
							data={[
								{ value: "cells", label: t("pages.benchmarks.cells.view", "Combinations") },
								{ value: "runs", label: t("pages.benchmarks.cells.runsView", "Every run") },
							]}
							data-testid="benchmark-ranking-view"
						/>
					) : null}
					{showCells ? (
						<BenchmarkCellsTable
							cells={cellsQuery.data?.cells ?? []}
							cohort={cellsQuery.data?.cohort ?? runsQuery.data.cohort}
							scorableItemCount={cellsQuery.data?.scorableItemCount ?? leafItemCount}
							items={taskItemsQuery.data?.items ?? []}
							selectedRunIds={selectedRunIds}
							isActionPending={startRun.isPending}
							onToggleRun={toggleRun}
							onRerunCell={rerunCell}
						/>
					) : (
						<BenchmarkRunsTable
							runs={runs}
							pairwiseScores={pairwiseScores}
							cohort={runsQuery.data.cohort}
							selectedRunIds={selectedRunIds}
							totalCount={runsQuery.data.totalCount}
							isLoadingMore={runsQuery.isFetchingNextPage}
							onLoadMore={runsQuery.loadMore}
							isActionPending={rejudgeRun.isPending || deleteRun.isPending || measureFidelity.isPending}
							onToggleRun={toggleRun}
							onRejudgeRun={rejudgeOne}
							onMeasureFidelity={measureRunFidelity}
							onDeleteRun={removeRun}
						/>
					)}
					{showCells &&
					detail &&
					canComparePairedDeltas(cellsQuery.data?.cells.length ?? 0, cellsQuery.data?.scorableItemCount ?? 0) ? (
						<BenchmarkPairedDelta projectId={detail.id} cells={cellsQuery.data?.cells ?? []} />
					) : null}
					{/* One fit covers the cohort, so the matrix is mounted once here rather than under each run pane. */}
					{detail?.judge.mode === "pairwise" ? <BenchmarkPairwiseMatrix projectId={detail.id} /> : null}
					{selectedRunIds.length >= 2 ? <BenchmarkLaunchCompare runIds={selectedRunIds} /> : null}
					{/* Opened on demand: mounting the charts pulls the charting library, and an operator reading the table
				    has not asked for it. */}
					<Group>
						<Button
							variant="subtle"
							size="xs"
							leftSection={<IconChartHistogram size={14} />}
							onClick={() => setChartsOpen((current) => !current)}
							aria-expanded={chartsOpen}
							data-testid="benchmark-charts-toggle"
						>
							{chartsOpen ? t("pages.benchmarks.charts.hide", "Hide charts") : t("pages.benchmarks.charts.show", "Show charts")}
						</Button>
					</Group>
					{chartsOpen ? (
						<Suspense
							fallback={
								<Group gap="sm">
									<Loader size="sm" />
									<Text c="dimmed">{t("pages.benchmarks.charts.loading", "Loading charts…")}</Text>
								</Group>
							}
						>
							<BenchmarkCharts runs={runs} selectedRuns={selectedRunDetails.runs} />
						</Suspense>
					) : null}
					<SimpleGrid cols={{ base: 1, lg: selectedRunIds.length > 1 ? 2 : 1 }}>
						{selectedRunIds.map((runId) => (
							<BenchmarkRunLivePane key={runId} runId={runId} />
						))}
					</SimpleGrid>
				</SectionCard>
			) : null}
		</>
	);
}
