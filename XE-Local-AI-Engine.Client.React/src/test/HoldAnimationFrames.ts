import { act } from "@testing-library/react";
import { vi } from "vitest";

/**
 * Queues every `requestAnimationFrame` callback until `runFrame` instead of running it on a timer, so a test can tell
 * a per-frame commit from an immediate flush. The suite's `restoreMocks` undoes the spies before the next test.
 */
export function holdAnimationFrames(): { runFrame: () => void } {
	const queued = new Map<number, FrameRequestCallback>();
	let lastId = 0;
	vi.spyOn(globalThis, "requestAnimationFrame").mockImplementation((callback) => {
		lastId += 1;
		queued.set(lastId, callback);
		return lastId;
	});
	vi.spyOn(globalThis, "cancelAnimationFrame").mockImplementation((id) => {
		queued.delete(id);
	});
	return {
		runFrame(): void {
			const callbacks = [...queued.values()];
			queued.clear();
			act(() => {
				for (const callback of callbacks) {
					callback(performance.now());
				}
			});
		},
	};
}
