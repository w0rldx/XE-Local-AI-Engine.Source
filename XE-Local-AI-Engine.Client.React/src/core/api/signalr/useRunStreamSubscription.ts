import { HubConnectionState } from "@microsoft/signalr";
import { useEffect, useLayoutEffect, useRef } from "react";
import { z } from "zod";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { joinHubGroup } from "@/core/api/signalr/SharedHubGroups";

export const runStreamEventSchema = z.object({
	runId: z.string(),
	sequence: z.number(),
	// Every member of the server's TrainingRunEventKind, or the frame is dropped: "Export" rides this stream because the
	// run's own status never moves for an export, and the two Evaluation kinds because an evaluation rides its run's
	// group rather than one of its own. scripts/CheckSignalrProxySync.mjs holds this list to the C# enum.
	kind: z.enum(["State", "Phase", "Progress", "Artifact", "Error", "EvaluationState", "EvaluationProgress", "Export"]),
	payload: z.object({
		state: z.string().nullish(),
		phase: z.string().nullish(),
		step: z.number().nullish(),
		totalSteps: z.number().nullish(),
		epoch: z.number().nullish(),
		loss: z.number().nullish(),
		learningRate: z.number().nullish(),
		vramBytes: z.number().nullish(),
		message: z.string().nullish(),
		runVersion: z.number().nullish(),
	}),
});

const runStreamReplayResetSchema = z.object({
	runId: z.string(),
	latestSequence: z.number(),
	runVersion: z.number(),
});

export type RunStreamEvent = z.infer<typeof runStreamEventSchema>;

export interface RunStreamHandlers {
	/** An event past the cursor. `gap` means sequences were skipped, so the view may lack what they carried. */
	onEvent: (event: RunStreamEvent, gap: boolean) => void;
	/** Only the HTTP snapshot can be trusted now: the server pushed a replay reset, or it refused the subscription. */
	onResync: () => void;
	/** True once the hub accepted this subscription; false while it is starting, reconnecting, closed or refused. */
	onLiveChange?: (live: boolean) => void;
}

/**
 * One id's stream on the run hub, which training runs and Model Lab experiments share. `Subscribe(id, afterSeq)` replays
 * what a reconnect missed, and the cursor drops an out-of-order or duplicate event. What an event means is the caller's.
 */
export function useRunStreamSubscription(runId: string | null, handlers: RunStreamHandlers): void {
	const cursorRef = useRef(0);
	const handlersRef = useRef(handlers);
	useLayoutEffect(() => {
		handlersRef.current = handlers;
	});

	useEffect(() => {
		cursorRef.current = 0;
		if (!runId) {
			return;
		}

		const hub = acquireHubConnection("training/runs/hub");
		const { connection } = hub;
		let disposed = false;

		const eventHandler = (value: unknown): void => {
			const parsed = runStreamEventSchema.safeParse(value);
			if (disposed || !parsed.success || parsed.data.runId !== runId || parsed.data.sequence <= cursorRef.current) {
				return;
			}
			const gap = parsed.data.sequence !== cursorRef.current + 1;
			cursorRef.current = parsed.data.sequence;
			handlersRef.current.onEvent(parsed.data, gap);
		};
		const resetHandler = (value: unknown): void => {
			const parsed = runStreamReplayResetSchema.safeParse(value);
			if (parsed.success && parsed.data.runId === runId) {
				cursorRef.current = parsed.data.latestSequence;
				handlersRef.current.onResync();
			}
		};

		connection.on("trainingRun.event", eventHandler);
		connection.on("trainingRun.replayReset", resetHandler);
		const degrade = (): void => {
			if (!disposed) {
				handlersRef.current.onLiveChange?.(false);
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
				.invoke("Subscribe", runId, cursorRef.current)
				.then(() => {
					if (!disposed) {
						handlersRef.current.onLiveChange?.(true);
					}
				})
				.catch(() => {
					degrade();
					handlersRef.current.onResync();
				});
		};
		hub.onReconnecting(degrade);
		hub.onClosed(degrade);
		// The operations feed may hold this run's group on the same connection; only the last holder leaves it.
		const leave = joinHubGroup(hub, ["Unsubscribe", runId], subscribe);
		hub.whenStarted.catch(degrade);

		return () => {
			disposed = true;
			handlersRef.current.onLiveChange?.(false);
			connection.off("trainingRun.event", eventHandler);
			connection.off("trainingRun.replayReset", resetHandler);
			leave();
			hub.release();
		};
	}, [runId]);
}
