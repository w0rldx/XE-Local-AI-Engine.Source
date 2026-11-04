import { HubConnectionState } from "@microsoft/signalr";
import { useEffect, useLayoutEffect, useRef, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";

export interface ModelLabLiveProgress {
	phase: string | null;
	step: number | null;
	totalSteps: number | null;
	vramBytes: number | null;
}

export interface ModelLabExperimentHubState extends ModelLabLiveProgress {
	/** True once the hub has accepted this subscription; false while it is starting, reconnecting or closed, so callers poll. */
	isLive: boolean;
}

interface RunEvent {
	runId: string;
	sequence: number;
	kind: string;
	payload: { phase?: string | null; step?: number | null; totalSteps?: number | null; vramBytes?: number | null };
}

const emptyProgress: ModelLabLiveProgress = { phase: null, step: null, totalSteps: null, vramBytes: null };

function isRunEvent(value: unknown): value is RunEvent {
	const candidate = value as Partial<RunEvent> | null;
	return typeof candidate?.runId === "string" && typeof candidate.sequence === "number" && typeof candidate.payload === "object";
}

/**
 * One experiment's progress, over the run hub an experiment shares with training runs. `Subscribe(runId, afterSeq)` replays what a
 * reconnect missed. The saved row is the truth: a gap, a reset or a state change falls back to it through `onResync`.
 */
export function useModelLabExperimentHub(experimentId: string | null, onResync: () => void): ModelLabExperimentHubState {
	const [snapshot, setSnapshot] = useState({ experimentId, progress: emptyProgress });
	const [isLive, setIsLive] = useState(false);
	const cursorRef = useRef(0);
	const resyncRef = useRef(onResync);
	useLayoutEffect(() => {
		resyncRef.current = onResync;
	}, [onResync]);

	useEffect(() => {
		setSnapshot({ experimentId, progress: emptyProgress });
		cursorRef.current = 0;
		if (!experimentId) {
			return;
		}

		const hub = acquireHubConnection("training/runs/hub");
		const { connection } = hub;
		let disposed = false;

		const onEvent = (value: unknown): void => {
			if (disposed || !isRunEvent(value) || value.runId !== experimentId || value.sequence <= cursorRef.current) {
				return;
			}
			const gap = value.sequence !== cursorRef.current + 1;
			cursorRef.current = value.sequence;
			const { phase, step, totalSteps, vramBytes } = value.payload;
			setSnapshot((current) => ({
				experimentId,
				progress: {
					phase: phase ?? current.progress.phase,
					step: step ?? current.progress.step,
					totalSteps: totalSteps ?? current.progress.totalSteps,
					vramBytes: vramBytes ?? current.progress.vramBytes,
				},
			}));
			if (gap || value.kind === "State") {
				resyncRef.current();
			}
		};
		const onReset = (value: unknown): void => {
			const reset = value as { runId?: string; latestSequence?: number } | null;
			if (reset?.runId === experimentId) {
				cursorRef.current = reset.latestSequence ?? cursorRef.current;
				resyncRef.current();
			}
		};

		connection.on("trainingRun.event", onEvent);
		connection.on("trainingRun.replayReset", onReset);
		const degrade = (): void => {
			if (!disposed) {
				setIsLive(false);
			}
		};
		const subscribe = (): void => {
			if (disposed) {
				return;
			}
			if (connection.state !== HubConnectionState.Connected) {
				degrade();
				return;
			}
			connection
				.invoke("Subscribe", experimentId, cursorRef.current)
				.then(() => {
					if (!disposed) {
						setIsLive(true);
					}
				})
				.catch(() => {
					degrade();
					resyncRef.current();
				});
		};
		hub.onReconnecting(degrade);
		hub.onReconnected(subscribe);
		hub.onClosed(degrade);
		hub.whenStarted.then(subscribe).catch(degrade);

		return () => {
			disposed = true;
			setIsLive(false);
			connection.off("trainingRun.event", onEvent);
			connection.off("trainingRun.replayReset", onReset);
			if (connection.state === HubConnectionState.Connected) {
				connection.invoke("Unsubscribe", experimentId).catch(() => undefined);
			}
			hub.release();
		};
	}, [experimentId]);

	return { ...(snapshot.experimentId === experimentId ? snapshot.progress : emptyProgress), isLive };
}
