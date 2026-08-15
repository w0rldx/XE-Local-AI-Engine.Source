import { HubConnectionState } from "@microsoft/signalr";
import { useEffect, useRef, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import type { DatasetGenerationProgress } from "@/features/training/models/TrainingModels";
import {
	applyGenerationEvent,
	datasetGenerationEventSchema,
	datasetGenerationReplayResetSchema,
} from "@/features/training/models/TrainingModels";

export const datasetGenerationHubEvents = {
	event: "datasetGeneration.event",
	replayReset: "datasetGeneration.replayReset",
} as const;

const emptyProgress: DatasetGenerationProgress = { completed: 0, total: 0, rejected: 0, state: null };

/**
 * Subscribes to one dataset's bounded generation stream. Generation events are coarse progress rather than token
 * deltas, so this hook keeps only a cursor and a counter: an out-of-order or duplicate event is dropped, and a gap or
 * a server-pushed replay reset falls back to the authoritative HTTP snapshot (`onResync`) instead of guessing.
 */
export function useDatasetGenerationHub(datasetId: string | null, onResync: () => void): DatasetGenerationProgress {
	const [progress, setProgress] = useState<DatasetGenerationProgress>(emptyProgress);
	const cursorRef = useRef(0);
	const resyncRef = useRef(onResync);
	resyncRef.current = onResync;

	useEffect(() => {
		if (!datasetId) {
			setProgress(emptyProgress);
			cursorRef.current = 0;
			return;
		}

		setProgress(emptyProgress);
		cursorRef.current = 0;
		const hub = acquireHubConnection("training/datasets/hub");
		const { connection } = hub;
		let disposed = false;

		const eventHandler = (value: unknown): void => {
			const parsed = datasetGenerationEventSchema.safeParse(value);
			if (!parsed.success || parsed.data.datasetId !== datasetId || parsed.data.sequence <= cursorRef.current) {
				return;
			}
			if (parsed.data.sequence !== cursorRef.current + 1) {
				cursorRef.current = parsed.data.sequence;
				resyncRef.current();
				return;
			}
			cursorRef.current = parsed.data.sequence;
			setProgress((current) => applyGenerationEvent(current, parsed.data));
			if (parsed.data.kind === "State") {
				resyncRef.current();
			}
		};

		const resetHandler = (value: unknown): void => {
			const parsed = datasetGenerationReplayResetSchema.safeParse(value);
			if (parsed.success && parsed.data.datasetId === datasetId) {
				cursorRef.current = parsed.data.latestSequence;
				resyncRef.current();
			}
		};

		connection.on(datasetGenerationHubEvents.event, eventHandler);
		connection.on(datasetGenerationHubEvents.replayReset, resetHandler);

		const subscribe = (): void => {
			if (disposed || connection.state !== HubConnectionState.Connected) {
				return;
			}
			connection.invoke("Subscribe", datasetId, cursorRef.current).catch(() => resyncRef.current());
		};
		hub.whenStarted.then(subscribe);
		hub.onReconnected(subscribe);

		return () => {
			disposed = true;
			connection.off(datasetGenerationHubEvents.event, eventHandler);
			connection.off(datasetGenerationHubEvents.replayReset, resetHandler);
			if (connection.state === HubConnectionState.Connected) {
				connection.invoke("Unsubscribe", datasetId).catch(() => undefined);
			}
			hub.release();
		};
	}, [datasetId]);

	return progress;
}
