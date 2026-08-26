import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import type { BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import { maxComparedBenchmarkRuns, toggleBenchmarkRunSelection } from "@/features/benchmarks/models/BenchmarkModels";

/** Coordinates deep-linked, automatically selected, and operator-selected runs for compare and live views. */
export function useBenchmarkRunSelection(runs: readonly BenchmarkRunSummary[], baseModelName?: string, tunedModelName?: string) {
	const [selectedRunIds, setSelectedRunIds] = useState<string[]>([]);
	const linkedRunsApplied = useRef(false);
	const linkedRunIds = useMemo(
		() =>
			[baseModelName, tunedModelName]
				.map((name) => (name == null ? undefined : runs.find((run) => run.primaryModelName === name)?.id))
				.filter((id): id is string => id != null),
		[baseModelName, runs, tunedModelName],
	);

	useEffect(() => {
		if (linkedRunIds.length > 0) {
			if (!linkedRunsApplied.current) {
				linkedRunsApplied.current = true;
				setSelectedRunIds(linkedRunIds);
			}
			return;
		}
		const latest = runs.slice(0, 2).map((run) => run.id);
		setSelectedRunIds((current) => {
			const valid = current.filter((id) => runs.some((run) => run.id === id));
			let next: string[];
			if (latest[0] && !valid.includes(latest[0])) {
				next = [latest[0], ...valid].slice(0, maxComparedBenchmarkRuns);
			} else {
				next = valid.length > 0 ? valid.slice(0, maxComparedBenchmarkRuns) : latest;
			}
			return next.length === current.length && next.every((id, index) => id === current[index]) ? current : next;
		});
	}, [linkedRunIds, runs]);

	const selectRun = useCallback((id: string): void => {
		setSelectedRunIds((current) => [id, ...current.filter((item) => item !== id)].slice(0, maxComparedBenchmarkRuns));
	}, []);

	const toggleRun = useCallback((id: string): void => {
		setSelectedRunIds((current) => toggleBenchmarkRunSelection(current, id));
	}, []);

	return { selectedRunIds, selectRun, toggleRun };
}
