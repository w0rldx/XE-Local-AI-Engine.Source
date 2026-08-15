// @vitest-environment jsdom

import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { useStreamCommitScheduler } from "@/features/chat/hooks/useStreamCommitScheduler";

// The scheduler exists because a fast local model pushes more SignalR deltas per second than the browser can paint.
// Its contract: at most ONE commit per animation frame carrying the LATEST state, a terminal event never waits on a
// frame (`flush`), and nothing commits after the turn was abandoned or the owner unmounted (`cancel` / effect
// cleanup). Fake timers drive requestAnimationFrame so each of those is asserted deterministically rather than by
// waiting on a real frame.

/** One animation frame under fake timers (rAF is faked at ~16 ms by @sinonjs/fake-timers). */
function advanceOneFrame(): void {
	act(() => {
		vi.advanceTimersByTime(20);
	});
}

describe("useStreamCommitScheduler", () => {
	beforeEach(() => {
		vi.useFakeTimers();
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	it("collapses every schedule within one frame into a single commit of the latest state", () => {
		const commit = vi.fn();
		const { result } = renderHook(() => useStreamCommitScheduler<string>(commit));

		act(() => {
			result.current.schedule("a");
			result.current.schedule("ab");
			result.current.schedule("abc");
		});
		expect(commit).not.toHaveBeenCalled();

		advanceOneFrame();

		expect(commit).toHaveBeenCalledTimes(1);
		expect(commit).toHaveBeenCalledWith("abc");
	});

	it("commits once per frame across successive frames", () => {
		const commit = vi.fn();
		const { result } = renderHook(() => useStreamCommitScheduler<string>(commit));

		act(() => result.current.schedule("first"));
		advanceOneFrame();
		act(() => result.current.schedule("second"));
		advanceOneFrame();

		expect(commit.mock.calls).toEqual([["first"], ["second"]]);
	});

	// Without `merge` the newest state replaces the pending one; with it the states fold, which is how a caller that
	// derives a delta (rather than a full snapshot) keeps everything emitted inside one frame.
	it("folds coalesced states through the supplied merge function", () => {
		const commit = vi.fn();
		const { result } = renderHook(() =>
			useStreamCommitScheduler<string>(commit, (previous, next) => previous + next),
		);

		act(() => {
			result.current.schedule("a");
			result.current.schedule("b");
			result.current.schedule("c");
		});
		advanceOneFrame();

		expect(commit).toHaveBeenCalledExactlyOnceWith("abc");
	});

	// A terminal/error event must not be left waiting on the next frame, and it must not then commit a second time.
	it("flush commits immediately and cancels the queued frame", () => {
		const commit = vi.fn();
		const { result } = renderHook(() => useStreamCommitScheduler<string>(commit));

		act(() => {
			result.current.schedule("done");
			result.current.flush();
		});

		expect(commit).toHaveBeenCalledExactlyOnceWith("done");

		advanceOneFrame();

		expect(commit).toHaveBeenCalledTimes(1);
	});

	it("flush is a no-op when nothing is pending", () => {
		const commit = vi.fn();
		const { result } = renderHook(() => useStreamCommitScheduler<string>(commit));

		act(() => result.current.flush());
		advanceOneFrame();

		expect(commit).not.toHaveBeenCalled();
	});

	// The aborted/deleted-turn path: the pending state is dropped, and a later flush must not resurrect it.
	it("cancel drops the pending state without committing it", () => {
		const commit = vi.fn();
		const { result } = renderHook(() => useStreamCommitScheduler<string>(commit));

		act(() => {
			result.current.schedule("abandoned");
			result.current.cancel();
		});
		advanceOneFrame();
		act(() => result.current.flush());

		expect(commit).not.toHaveBeenCalled();
	});

	it("does not commit after the owner unmounts mid-stream", () => {
		const commit = vi.fn();
		const { result, unmount } = renderHook(() => useStreamCommitScheduler<string>(commit));

		act(() => result.current.schedule("in flight"));
		unmount();
		advanceOneFrame();

		expect(commit).not.toHaveBeenCalled();
	});

	// `commit` is read through a ref precisely so the returned callbacks stay referentially stable — the reason the
	// docs say they are safe to list in a dependency array. Both halves of that claim are asserted here.
	it("keeps the callbacks stable across renders while committing through the newest callback", () => {
		const first = vi.fn();
		const second = vi.fn();
		const { result, rerender } = renderHook(({ commit }) => useStreamCommitScheduler<string>(commit), {
			initialProps: { commit: first },
		});
		const initial = result.current;

		rerender({ commit: second });

		expect(result.current).toBe(initial);
		expect(result.current.schedule).toBe(initial.schedule);

		act(() => {
			result.current.schedule("latest");
			result.current.flush();
		});

		expect(first).not.toHaveBeenCalled();
		expect(second).toHaveBeenCalledExactlyOnceWith("latest");
	});
});
