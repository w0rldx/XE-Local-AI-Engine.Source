// @vitest-environment jsdom

import { act, renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { useRovingTabStop } from "@/features/modelLab/models/ModelLabFocus";

function stops(roving: { tabIndexFor: (index: number) => 0 | -1 }, count: number): number[] {
	return Array.from({ length: count }, (_, index) => roving.tabIndexFor(index));
}

describe("useRovingTabStop", () => {
	it("keeps one Tab stop in the row: the last focused button, the selected one, else the first", () => {
		const { result, rerender } = renderHook(({ selected, count }) => useRovingTabStop(selected, count), {
			initialProps: { selected: -1, count: 3 },
		});
		expect(stops(result.current, 3)).toEqual([0, -1, -1]);

		act(() => result.current.onFocusAt(2));
		expect(stops(result.current, 3)).toEqual([-1, -1, 0]);

		// A new selection takes the stop back.
		rerender({ selected: 1, count: 3 });
		expect(stops(result.current, 3)).toEqual([-1, 0, -1]);

		// A focused button the row no longer has gives the stop to the selected one, then to the first.
		act(() => result.current.onFocusAt(2));
		rerender({ selected: 1, count: 2 });
		expect(stops(result.current, 2)).toEqual([-1, 0]);
		rerender({ selected: 5, count: 2 });
		expect(stops(result.current, 2)).toEqual([0, -1]);
	});
});
