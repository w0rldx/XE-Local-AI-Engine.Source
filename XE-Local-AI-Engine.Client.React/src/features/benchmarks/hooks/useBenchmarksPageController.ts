import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { useBenchmarkProjectEditor } from "@/features/benchmarks/hooks/useBenchmarkProjectEditor";
import { useBenchmarkRunActions } from "@/features/benchmarks/hooks/useBenchmarkRunActions";
import { useBenchmarkRunSelection } from "@/features/benchmarks/hooks/useBenchmarkRunSelection";
import { benchmarkRunEstimate, medianBenchmarkRunDurationMs } from "@/features/benchmarks/models/BenchmarkRunEstimate";
import { leafBenchmarkTaskItems } from "@/features/benchmarks/models/BenchmarkTaskItems";
import {
	useBenchmarkCells,
	useBenchmarkComparisons,
	useBenchmarkProject,
	useBenchmarkProjects,
	useBenchmarkRunDetails,
	useBenchmarkRuns,
	useBenchmarkTaskItems,
} from "@/features/benchmarks/queries/useBenchmarks";

export interface BenchmarksPageProps {
	readonly baseModelName?: string;
	readonly tunedModelName?: string;
}

// The benchmarks page reads one project at a time, so this hook owns that selection and the queries hanging off it —
// the project, its runs, its task items, its cells and its pairwise comparisons — plus how the results are ranked and
// which runs are compared. The two mutating concerns live next door and are composed in below: the project editor
// (`useBenchmarkProjectEditor`) and the launch configuration with every run mutation (`useBenchmarkRunActions`).
// Both take the selected project and its runs as inputs and own their own state, so nothing is threaded back.
// The returned object is one flat bag by design: the page hands it whole to the three workspace components.
export function useBenchmarksPageController({ baseModelName, tunedModelName }: BenchmarksPageProps = {}) {
	const { t } = useTranslation();
	const projectsQuery = useBenchmarkProjects();
	const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);
	const projectQuery = useBenchmarkProject(selectedProjectId);
	const detail = projectQuery.data;
	const runsQuery = useBenchmarkRuns(selectedProjectId);
	const runs = useMemo(() => runsQuery.data?.items ?? [], [runsQuery.data]);
	const taskItemsQuery = useBenchmarkTaskItems(selectedProjectId);
	const leafItemCount = Math.max(leafBenchmarkTaskItems(taskItemsQuery.data?.items ?? []).length, 1);
	const medianRunMs = useMemo(() => medianBenchmarkRunDurationMs(runs), [runs]);
	const singleRunEstimate = benchmarkRunEstimate({ cellCount: 1, leafItemCount, repeatCount: 1, warmup: false }, medianRunMs);
	const isSuite = leafItemCount > 1;
	const cellsQuery = useBenchmarkCells(selectedProjectId, isSuite);
	const [ranking, setRanking] = useState<"cells" | "runs">("cells");
	const showCells = isSuite && ranking === "cells";
	// Only read while the project actually judges pairwise; shares the query key the matrix below uses, so the two are
	// one request. A fit that is not current yields no intervals — a stale band is worse than none.
	const isPairwise = projectQuery.data?.judge.mode === "pairwise";
	const comparisonsQuery = useBenchmarkComparisons(selectedProjectId, isPairwise);
	const pairwiseScores = useMemo(() => {
		const fit = comparisonsQuery.data?.fit;
		return fit?.isCurrent ? new Map(fit.scores.map((score) => [score.runId, score])) : undefined;
	}, [comparisonsQuery.data]);
	const { selectedRunIds, selectRun, toggleRun } = useBenchmarkRunSelection(runs, baseModelName, tunedModelName);
	// Already in cache for the compare view and the live panes; read here for the frozen reasoning budget, which the
	// list projection does not carry.
	const selectedRunDetails = useBenchmarkRunDetails(selectedRunIds);
	const [chartsOpen, setChartsOpen] = useState(false);
	const runActions = useBenchmarkRunActions({ detail, selectedProjectId, runs, selectRun });
	const { resetKvCacheType } = runActions;
	const selectProject = useCallback(
		(projectId: string | null) => {
			setSelectedProjectId(projectId);
			resetKvCacheType();
		},
		[resetKvCacheType],
	);
	const editor = useBenchmarkProjectEditor({ detail, runs, onProjectCreated: selectProject });

	useEffect(() => {
		if (!selectedProjectId && projectsQuery.data?.[0]) {
			selectProject(projectsQuery.data[0].id);
		}
		if (selectedProjectId && projectsQuery.data && !projectsQuery.data.some((project) => project.id === selectedProjectId)) {
			selectProject(projectsQuery.data[0]?.id ?? null);
		}
	}, [projectsQuery.data, selectedProjectId, selectProject]);

	return {
		t,
		projectsQuery,
		selectedProjectId,
		selectProject,
		projectQuery,
		detail,
		runsQuery,
		runs,
		pairwiseScores,
		selectedRunIds,
		toggleRun,
		selectRun,
		selectedRunDetails,
		chartsOpen,
		setChartsOpen,
		taskItemsQuery,
		leafItemCount,
		medianRunMs,
		singleRunEstimate,
		isSuite,
		cellsQuery,
		ranking,
		setRanking,
		showCells,
		...runActions,
		...editor,
	};
}

export type BenchmarksPageController = ReturnType<typeof useBenchmarksPageController>;
