// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { useBenchmarkRunSelection } from "@/features/benchmarks/hooks/useBenchmarkRunSelection";
import type { BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";

const run = (id: string, primaryModelName = id): BenchmarkRunSummary => ({ id, primaryModelName }) as BenchmarkRunSummary;

describe("useBenchmarkRunSelection", () => {
	it("keeps an explicit deep-link selection instead of replacing it with the newest run", async () => {
		const runs = [run("newest"), run("base-run", "base"), run("tuned-run", "tuned")];
		const { result, rerender } = renderHook(({ items }) => useBenchmarkRunSelection(items, "base", "tuned"), {
			initialProps: { items: runs },
		});
		await waitFor(() => expect(result.current.selectedRunIds).toEqual(["base-run", "tuned-run"]));

		rerender({ items: [run("even-newer"), ...runs] });
		expect(result.current.selectedRunIds).toEqual(["base-run", "tuned-run"]);
	});

	it("deduplicates a run selected after automatic selection", async () => {
		const { result } = renderHook(() => useBenchmarkRunSelection([run("run-1"), run("run-2")]));
		await waitFor(() => expect(result.current.selectedRunIds).toEqual(["run-1"]));

		act(() => result.current.selectRun("run-1"));
		expect(result.current.selectedRunIds).toEqual(["run-1"]);
	});
});
