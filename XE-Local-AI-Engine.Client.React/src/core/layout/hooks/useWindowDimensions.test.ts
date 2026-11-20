// @vitest-environment jsdom

import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";

function resizeTo(width: number, height: number) {
	Object.defineProperty(window, "innerWidth", { configurable: true, value: width });
	Object.defineProperty(window, "innerHeight", { configurable: true, value: height });
	window.dispatchEvent(new Event("resize"));
}

describe("useWindowDimensions", () => {
	afterEach(() => {
		vi.restoreAllMocks();
	});

	// Sixteen components read the window size; each used to hold its own listener and its own state.
	it("shares one resize listener between consumers and removes it after the last unmounts", () => {
		const add = vi.spyOn(window, "addEventListener");
		const remove = vi.spyOn(window, "removeEventListener");
		const resizeCalls = (spy: typeof add) => spy.mock.calls.filter(([type]) => type === "resize").length;

		const first = renderHook(() => useWindowDimensions());
		const second = renderHook(() => useWindowDimensions());
		expect(resizeCalls(add)).toBe(1);

		first.unmount();
		expect(resizeCalls(remove)).toBe(0);

		second.unmount();
		expect(resizeCalls(remove)).toBe(1);
	});

	it("re-renders every consumer with the new size on resize", () => {
		const first = renderHook(() => useWindowDimensions());
		const second = renderHook(() => useWindowDimensions());

		act(() => resizeTo(390, 844));

		expect(first.result.current).toEqual({ width: 390, height: 844 });
		expect(second.result.current).toBe(first.result.current);

		first.unmount();
		second.unmount();
	});

	it("reads the current size on mount after a resize nobody was listening to", () => {
		resizeTo(1280, 720);

		const { result, unmount } = renderHook(() => useWindowDimensions());

		expect(result.current).toEqual({ width: 1280, height: 720 });
		unmount();
	});
});
