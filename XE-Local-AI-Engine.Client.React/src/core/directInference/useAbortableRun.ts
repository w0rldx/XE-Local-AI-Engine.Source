import { useEffect, useRef, useState } from "react";

export interface AbortableRun {
	running: boolean;
	/** Runs `task` with a fresh signal. `isCurrent` turns false once a newer run starts or the owner unmounts. */
	start: (task: (signal: AbortSignal, isCurrent: () => boolean) => Promise<void>) => Promise<void>;
	/** Idle at once: while the hub is still connecting, the attempt only settles when the connection does. */
	stop: () => void;
}

/** One streamed run at a time that Stop and unmount cancel. What the run sends and shows is the caller's. */
export function useAbortableRun(): AbortableRun {
	const [running, setRunning] = useState(false);
	const abortRef = useRef<AbortController | undefined>(undefined);
	// Leaving mid-run cancels it, or it keeps holding the node's invocation slot.
	useEffect(
		() => () => {
			abortRef.current?.abort();
			abortRef.current = undefined;
		},
		[],
	);

	const start = async (task: (signal: AbortSignal, isCurrent: () => boolean) => Promise<void>): Promise<void> => {
		const controller = new AbortController();
		abortRef.current = controller;
		setRunning(true);
		try {
			await task(controller.signal, () => abortRef.current === controller);
		} finally {
			// A stopped attempt already went idle, and by now the owner may belong to a newer run. A task that throws
			// goes idle too, and its error still reaches the caller.
			if (!controller.signal.aborted) {
				setRunning(false);
				abortRef.current = undefined;
			}
		}
	};

	const stop = (): void => {
		abortRef.current?.abort();
		setRunning(false);
	};

	return { running, start, stop };
}
