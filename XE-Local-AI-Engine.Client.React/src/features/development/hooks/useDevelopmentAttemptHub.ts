import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import type {
	DevelopmentAttemptLiveUpdate,
	DevelopmentAttemptSubscriptionSnapshot,
} from "@/features/development/models/DevelopmentModels";
import { developmentInvalidationKey, developmentQueryIds } from "@/features/development/queries/useDevelopment";

const EVENT_NAME = "developmentAttemptUpdate";
const MAX_RETAINED_UPDATES = 200;

export interface DevelopmentAttemptLiveState {
	readonly connectionState: "idle" | "connecting" | "connected" | "reconnecting" | "unavailable";
	readonly watermark: number;
	readonly droppedOrCoalescedUpdateCount: number;
	readonly latest: DevelopmentAttemptLiveUpdate | null;
	readonly updates: readonly DevelopmentAttemptLiveUpdate[];
}

const emptyState: DevelopmentAttemptLiveState = {
	connectionState: "idle",
	watermark: 0,
	droppedOrCoalescedUpdateCount: 0,
	latest: null,
	updates: [],
};

export function useDevelopmentAttemptHub(projectId: string | null, taskId: string | null, attemptId: string | null) {
	const queryClient = useQueryClient();
	const [state, setState] = useState<DevelopmentAttemptLiveState>(emptyState);

	useEffect(() => {
		if (!projectId || !taskId || !attemptId) {
			setState(emptyState);
			return;
		}

		const hub = acquireHubConnection("development/hub");
		const { connection } = hub;
		let disposed = false;
		let snapshotResolved = false;
		let watermark = 0;
		let buffered: DevelopmentAttemptLiveUpdate[] = [];
		const seenSequences = new Set<number>();

		setState({ ...emptyState, connectionState: "connecting" });

		const retain = (update: DevelopmentAttemptLiveUpdate): void => {
			if (
				update.projectId !== projectId ||
				update.taskId !== taskId ||
				update.attemptId !== attemptId ||
				seenSequences.has(update.sequence)
			) {
				return;
			}
			seenSequences.add(update.sequence);
			watermark = Math.max(watermark, update.sequence);
			setState((current) => ({
				...current,
				watermark,
				latest: update,
				updates: [...current.updates, update].slice(-MAX_RETAINED_UPDATES),
			}));
			if (update.kind === "Terminal") {
				queryClient
					.invalidateQueries({ queryKey: developmentInvalidationKey(developmentQueryIds.getProject) })
					.catch(() => undefined);
			}
		};

		const onUpdate = (update: DevelopmentAttemptLiveUpdate): void => {
			if (!snapshotResolved) {
				buffered.push(update);
				return;
			}
			if (update.sequence > watermark) {
				retain(update);
			}
		};

		const subscribe = async (reconnecting: boolean): Promise<void> => {
			if (disposed || connection.state !== HubConnectionState.Connected) {
				return;
			}
			snapshotResolved = false;
			buffered = [];
			setState((current) => ({ ...current, connectionState: reconnecting ? "reconnecting" : "connecting" }));
			try {
				const snapshot = await connection.invoke<DevelopmentAttemptSubscriptionSnapshot>(
					"SubscribeAsync",
					projectId,
					taskId,
					attemptId,
				);
				if (disposed) {
					return;
				}
				const retainedWatermark = watermark;
				watermark = Math.max(watermark, snapshot.watermark);
				setState((current) => ({
					...current,
					connectionState: "connected",
					watermark,
					droppedOrCoalescedUpdateCount: snapshot.droppedOrCoalescedUpdateCount,
				}));
				if (snapshot.latest && snapshot.latest.sequence > retainedWatermark && !seenSequences.has(snapshot.latest.sequence)) {
					retain(snapshot.latest);
				}
				snapshotResolved = true;
				for (const update of buffered.sort((left, right) => left.sequence - right.sequence)) {
					if (update.sequence > watermark) {
						retain(update);
					}
				}
				buffered = [];
			} catch {
				if (!disposed) {
					setState((current) => ({ ...current, connectionState: "unavailable" }));
				}
			}
		};

		connection.on(EVENT_NAME, onUpdate);
		const removeReconnect = hub.onReconnected(() => {
			subscribe(true).catch(() => undefined);
		});
		hub.whenStarted.then(() => subscribe(false)).catch(() => undefined);

		return () => {
			disposed = true;
			connection.off(EVENT_NAME, onUpdate);
			removeReconnect();
			hub.release();
		};
	}, [attemptId, projectId, queryClient, taskId]);

	return state;
}
