// @vitest-environment jsdom

import { act, renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { useAbortableRun } from "@/core/directInference/useAbortableRun";

describe("useAbortableRun", () => {
	it("goes idle when the task throws, and passes the error on", async () => {
		const { result } = renderHook(() => useAbortableRun());

		let outcome: Promise<void> = Promise.resolve();
		act(() => {
			outcome = result.current.start(() => Promise.reject(new Error("hub dropped")));
		});
		expect(result.current.running).toBe(true);

		await act(async () => {
			await expect(outcome).rejects.toThrow("hub dropped");
		});
		expect(result.current.running).toBe(false);
	});

	it("starting a new run leaves the previous run's signal untouched", async () => {
		const { result } = renderHook(() => useAbortableRun());
		const signals: AbortSignal[] = [];
		const pending = new Promise<void>(() => undefined);

		const task = (signal: AbortSignal): Promise<void> => {
			signals.push(signal);
			return pending;
		};

		act(() => {
			result.current.start(task).catch(() => undefined);
			result.current.start(task).catch(() => undefined);
		});

		expect(signals.map((signal) => signal.aborted)).toEqual([false, false]);
	});
});
