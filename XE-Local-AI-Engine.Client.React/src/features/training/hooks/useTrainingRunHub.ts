import { HubConnectionState } from "@microsoft/signalr";
import { useEffect, useLayoutEffect, useRef, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import type { TrainingRunLiveProgress } from "@/features/training/models/TrainingModels";
import {
	applyRunEvent,
	emptyTrainingRunProgress,
	trainingRunEventSchema,
	trainingRunReplayResetSchema,
} from "@/features/training/models/TrainingModels";

// Module-private: nothing outside this hook subscribes to the run stream.
const trainingRunHubEvents = {
	event: "trainingRun.event",
	replayReset: "trainingRun.replayReset",
} as const;

/**
 * Subscribes to one run's bounded progress stream. A run publishes coarse progress rather than token deltas, so this
 * hook keeps only a cursor and the latest counters: an out-of-order or duplicate event is dropped, and a gap or a
 * server-pushed replay reset falls back to the authoritative HTTP snapshot (`onResync`) instead of guessing.
 */
export function useTrainingRunHub(runId: string | null, onResync: () => void): TrainingRunLiveProgress {
	const [progress, setProgress] = useState<TrainingRunLiveProgress>(emptyTrainingRunProgress);
	const cursorRef = useRef(0);
	const resyncRef = useRef(onResync);
	useLayoutEffect(() => {
		resyncRef.current = onResync;
	}, [onResync]);

	useEffect(() => {
		if (!runId) {
			setProgress(emptyTrainingRunProgress);
			cursorRef.current = 0;
			return;
		}

		setProgress(emptyTrainingRunProgress);
		cursorRef.current = 0;
		const hub = acquireHubConnection("training/runs/hub");
		const { connection } = hub;
		let disposed = false;

		const eventHandler = (value: unknown): void => {
			const parsed = trainingRunEventSchema.safeParse(value);
			if (!parsed.success || parsed.data.runId !== runId || parsed.data.sequence <= cursorRef.current) {
				return;
			}
			if (parsed.data.sequence !== cursorRef.current + 1) {
				cursorRef.current = parsed.data.sequence;
				resyncRef.current();
				return;
			}
			cursorRef.current = parsed.data.sequence;
			setProgress((current) => applyRunEvent(current, parsed.data));
			if (parsed.data.kind === "State") {
				// A status change moves the run row itself, which only the HTTP snapshot is authoritative for.
				resyncRef.current();
			}
		};

		const resetHandler = (value: unknown): void => {
			const parsed = trainingRunReplayResetSchema.safeParse(value);
			if (parsed.success && parsed.data.runId === runId) {
				cursorRef.current = parsed.data.latestSequence;
				resyncRef.current();
			}
		};

		connection.on(trainingRunHubEvents.event, eventHandler);
		connection.on(trainingRunHubEvents.replayReset, resetHandler);

		const subscribe = (): void => {
			if (disposed || connection.state !== HubConnectionState.Connected) {
				return;
			}
			connection.invoke("Subscribe", runId, cursorRef.current).catch(() => resyncRef.current());
		};
		hub.whenStarted.then(subscribe);
		hub.onReconnected(subscribe);

		return () => {
			disposed = true;
			connection.off(trainingRunHubEvents.event, eventHandler);
			connection.off(trainingRunHubEvents.replayReset, resetHandler);
			if (connection.state === HubConnectionState.Connected) {
				connection.invoke("Unsubscribe", runId).catch(() => undefined);
			}
			hub.release();
		};
	}, [runId]);

	return progress;
}
