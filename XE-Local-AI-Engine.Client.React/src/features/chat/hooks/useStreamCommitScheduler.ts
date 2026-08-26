import { useCallback, useEffect, useLayoutEffect, useMemo, useRef } from "react";

export interface StreamCommitScheduler<TState> {
	// Record the latest pending state and ensure a single requestAnimationFrame flush is queued. Repeated calls
	// within one frame collapse to one commit; when `merge` is supplied the states are folded rather than replaced.
	schedule: (state: TState) => void;
	// Commit the latest pending state immediately, cancelling any queued frame. No-op when nothing is pending.
	// Use this for terminal/error events so completion is never left waiting on the next frame.
	flush: () => void;
	// Drop any pending state without committing (aborted/deleted turn, or component unmount).
	cancel: () => void;
}

function takeLatestState<TState>(_previous: TState, next: TState): TState {
	return next;
}

/**
 * Coalesces high-frequency stream commits into at most one React commit per animation frame. A fast local model
 * can push more SignalR deltas per second than the browser can paint; committing each one synchronously floods
 * React with renders. The caller keeps its reducer synchronous and pure (each event folds onto the previous
 * state), and hands the derived state here — only the setState / query-cache side effects are deferred to a
 * `requestAnimationFrame` flush of the LATEST pending state.
 *
 * `commit` and `merge` are read through refs so the returned callbacks stay referentially stable (safe to list in
 * dependency arrays) even as their closures capture fresh state each render.
 */
export function useStreamCommitScheduler<TState>(
	commit: (state: TState) => void,
	merge: (previous: TState, next: TState) => TState = takeLatestState,
): StreamCommitScheduler<TState> {
	const pendingRef = useRef<{ state: TState } | undefined>(undefined);
	const frameRef = useRef<number | undefined>(undefined);
	const commitRef = useRef(commit);
	const mergeRef = useRef(merge);
	useLayoutEffect(() => {
		commitRef.current = commit;
		mergeRef.current = merge;
	}, [commit, merge]);

	const runFlush = useCallback(() => {
		frameRef.current = undefined;
		const pending = pendingRef.current;
		if (!pending) {
			return;
		}

		pendingRef.current = undefined;
		commitRef.current(pending.state);
	}, []);

	const schedule = useCallback(
		(state: TState) => {
			pendingRef.current = {
				state: pendingRef.current ? mergeRef.current(pendingRef.current.state, state) : state,
			};
			if (frameRef.current === undefined) {
				frameRef.current = requestAnimationFrame(runFlush);
			}
		},
		[runFlush],
	);

	const flush = useCallback(() => {
		if (frameRef.current !== undefined) {
			cancelAnimationFrame(frameRef.current);
			frameRef.current = undefined;
		}
		runFlush();
	}, [runFlush]);

	const cancel = useCallback(() => {
		if (frameRef.current !== undefined) {
			cancelAnimationFrame(frameRef.current);
			frameRef.current = undefined;
		}
		pendingRef.current = undefined;
	}, []);

	// Drop a queued frame if the owner unmounts mid-stream so the flush can't fire after teardown.
	useEffect(() => cancel, [cancel]);

	return useMemo(() => ({ schedule, flush, cancel }), [schedule, flush, cancel]);
}
