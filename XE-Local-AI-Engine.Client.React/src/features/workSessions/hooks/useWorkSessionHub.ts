import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";
import type { WorkSessionEventResponse } from "@/features/workSessions/models/WorkSessionModels";
import { workSessionInvalidationKey, workSessionQueryIds } from "@/features/workSessions/queries/useWorkSessions";

const HUB_PATH = "work-sessions/hub";
const EVENT_NAME = "workSessionChanged";
/** The dev-mode fallback cadence, reused: fast enough to feel live, slow enough not to hammer a broken hub. */
export const WORK_SESSION_POLL_INTERVAL_MS = 3_000;

/**
 * The change kinds the hub pushes. **Lowercase on the wire** — P3's publisher serializes
 * `kind.ToString().ToLowerInvariant()`, so a casing slip here makes every invalidation a silent no-op rather than an
 * error. The literals are asserted in `useWorkSessionHub.test.tsx` for exactly that reason.
 */
const workSessionChangeKinds = ["status", "step", "task", "finding", "artifact", "checkpoint"] as const;
export type WorkSessionChangeKind = (typeof workSessionChangeKinds)[number];

export interface WorkSessionChanged {
	readonly sessionId: string;
	readonly seq: number;
	readonly kind: string;
}

/** P3 §6. `events` is the replay since the client's watermark, capped at 200 with `replayTruncated`. */
export interface WorkSessionSubscriptionSnapshot {
	readonly sessionId: string;
	readonly status: string;
	readonly step: number;
	readonly currentTaskId: string | null;
	readonly lastSeq: number;
	readonly events: readonly WorkSessionEventResponse[];
	readonly replayTruncated: boolean;
}

export interface WorkSessionLiveState {
	readonly connectionState: "idle" | "connecting" | "connected" | "reconnecting" | "unavailable";
	/** Painted from the snapshot before the detail query lands; `undefined` until the first subscribe answers. */
	readonly status?: string;
	readonly step?: number;
	readonly currentTaskId?: string | null;
	/** Highest event sequence seen. Passed as `afterSeq` on every (re)subscribe. */
	readonly watermark: number;
	/** Bumped on every `step` push so the embedded `Chat` re-arms its re-attach for the new server-side turn. */
	readonly resumeNonce: number;
	/** Polling cadence for the page's queries while the hub is down; `undefined` while it is live. */
	readonly pollIntervalMs?: number;
}

const emptyState: WorkSessionLiveState = { connectionState: "idle", watermark: 0, resumeNonce: 0 };

function isChangeKind(kind: string): kind is WorkSessionChangeKind {
	return (workSessionChangeKinds as readonly string[]).includes(kind);
}

/**
 * Live session state over `work-sessions/hub`.
 *
 * Shape borrowed from `useDevelopmentAttemptHub` (connection-state machine, pre-snapshot buffering, `seq` dedupe,
 * re-subscribe on reconnect, `release()` on unmount), but the subscribe contract is genuinely different and is
 * implemented, not inherited: `SubscribeSession(sessionId, afterSeq)` takes the client's watermark, and the snapshot
 * carries the replay rather than a single latest update.
 *
 * **What the replay is used for.** The hub pushes notifications, never payloads (P3 L2), and a replayed
 * `WorkSessionEventResponse` carries an `eventType` (`StepStarted`, …), not a change *kind* — so the missed feeds
 * cannot be derived from it. The replay's real jobs are therefore the watermark and the fact that something changed
 * while this client was away: a non-empty replay (or `replayTruncated`) invalidates every feed of this session once,
 * and the store — which P3 L1 makes the replay authority — answers the refetch. Pushed events, which DO carry a
 * kind, drive the fine-grained per-feed invalidation below.
 */
export function useWorkSessionHub(sessionId: string | undefined, conversationId: string | undefined): WorkSessionLiveState {
	const queryClient = useQueryClient();
	const [state, setState] = useState<WorkSessionLiveState>(emptyState);

	useEffect(() => {
		if (!sessionId) {
			setState(emptyState);
			return;
		}

		const hub = acquireHubConnection(HUB_PATH);
		const { connection } = hub;
		let disposed = false;
		let snapshotResolved = false;
		let watermark = 0;
		let buffered: WorkSessionChanged[] = [];
		const seenSequences = new Set<number>();

		setState({ ...emptyState, connectionState: "connecting" });

		const invalidate = (queryKey: readonly unknown[]): void => {
			queryClient.invalidateQueries({ queryKey }).catch(() => undefined);
		};

		const invalidateEveryFeed = (): void => {
			for (const operationId of [
				workSessionQueryIds.get,
				workSessionQueryIds.tasks,
				workSessionQueryIds.findings,
				workSessionQueryIds.artifacts,
				workSessionQueryIds.checkpoints,
				workSessionQueryIds.events,
			]) {
				invalidate(workSessionInvalidationKey(operationId, sessionId));
			}
		};

		const apply = (change: WorkSessionChanged): void => {
			if (change.sessionId !== sessionId || seenSequences.has(change.seq)) {
				return;
			}
			seenSequences.add(change.seq);
			watermark = Math.max(watermark, change.seq);
			// Every kind moves the append-only event feed.
			invalidate(workSessionInvalidationKey(workSessionQueryIds.events, sessionId));
			const kind = isChangeKind(change.kind) ? change.kind : undefined;
			switch (kind) {
				case "status":
					invalidate(workSessionInvalidationKey(workSessionQueryIds.get, sessionId));
					invalidate(workSessionInvalidationKey(workSessionQueryIds.list));
					break;
				case "step":
					invalidate(workSessionInvalidationKey(workSessionQueryIds.get, sessionId));
					if (conversationId) {
						invalidate(nodeChatQueryKeys.conversation(conversationId));
					}
					break;
				case "task":
					invalidate(workSessionInvalidationKey(workSessionQueryIds.tasks, sessionId));
					break;
				case "finding":
					invalidate(workSessionInvalidationKey(workSessionQueryIds.findings, sessionId));
					break;
				case "artifact":
					invalidate(workSessionInvalidationKey(workSessionQueryIds.artifacts, sessionId));
					break;
				case "checkpoint":
					invalidate(workSessionInvalidationKey(workSessionQueryIds.checkpoints, sessionId));
					break;
				default:
					// An unknown kind (a newer server) must still refresh SOMETHING rather than be dropped silently.
					invalidateEveryFeed();
					break;
			}
			setState((current) => ({
				...current,
				watermark,
				// The step push is published at step START (X8), while the invocation is still resumable — this is what
				// makes the embedded conversation stream live instead of back-filling a beat later.
				resumeNonce: kind === "step" ? current.resumeNonce + 1 : current.resumeNonce,
			}));
		};

		const onChanged = (change: WorkSessionChanged): void => {
			if (!snapshotResolved) {
				buffered.push(change);
				return;
			}
			if (change.seq > watermark) {
				apply(change);
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
				// Two arguments, and the watermark is re-sent on EVERY reconnect: the store serves a client that has
				// been away for days, so there is no buffer to roll over and no `replayReset` to handle.
				const snapshot = await connection.invoke<WorkSessionSubscriptionSnapshot>("SubscribeSession", sessionId, watermark);
				if (disposed) {
					return;
				}
				const missedSomething = snapshot.replayTruncated || snapshot.events.length > 0 || snapshot.lastSeq > watermark;
				watermark = Math.max(watermark, snapshot.lastSeq);
				setState((current) => ({
					...current,
					connectionState: "connected",
					status: snapshot.status,
					step: snapshot.step,
					currentTaskId: snapshot.currentTaskId,
					watermark,
					pollIntervalMs: undefined,
				}));
				if (missedSomething) {
					invalidateEveryFeed();
				}
				snapshotResolved = true;
				for (const change of buffered.toSorted((left, right) => left.seq - right.seq)) {
					if (change.seq > watermark) {
						apply(change);
					}
				}
				buffered = [];
			} catch {
				if (!disposed) {
					// Never a blocking error: the page falls back to polling and keeps rendering last-good state.
					setState((current) => ({
						...current,
						connectionState: "unavailable",
						pollIntervalMs: WORK_SESSION_POLL_INTERVAL_MS,
					}));
				}
			}
		};

		connection.on(EVENT_NAME, onChanged);
		const removeReconnect = hub.onReconnected(() => {
			subscribe(true).catch(() => undefined);
		});
		hub.whenStarted.then(() => subscribe(false)).catch(() => undefined);

		return () => {
			disposed = true;
			connection.off(EVENT_NAME, onChanged);
			removeReconnect();
			if (connection.state === HubConnectionState.Connected) {
				connection.invoke("UnsubscribeSession", sessionId).catch(() => undefined);
			}
			hub.release();
		};
	}, [conversationId, queryClient, sessionId]);

	return state;
}
