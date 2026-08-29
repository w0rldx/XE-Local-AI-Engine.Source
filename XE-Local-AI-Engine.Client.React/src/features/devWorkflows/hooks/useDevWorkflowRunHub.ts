import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import type { DevWorkflowRunEventResponse } from "@/features/devWorkflows/models/DevWorkflowModels";
import { devWorkflowInvalidationKey, devWorkflowQueryIds } from "@/features/devWorkflows/queries/useDevWorkflows";

const HUB_PATH = "development-workflows/hub";
const EVENT_NAME = "devWorkflowChanged";
/** The work-session fallback cadence, reused: fast enough to feel live, slow enough not to hammer a broken hub. */
export const DEV_WORKFLOW_POLL_INTERVAL_MS = 3_000;

/**
 * X19: exactly four kinds, **LOWERCASE on the wire** — the publisher serializes the kind in lower case, so a casing
 * slip here makes every invalidation a silent no-op rather than an error. The literals are asserted in
 * `useDevWorkflowRunHub.test.tsx` for exactly that reason.
 *
 * `graph` and `event` are deliberately absent: a materialization folds into a `node` ping (Y12 — the client refetches
 * the run, which carries the nodes and the bumped graph revision), and every kind invalidates the event feed anyway.
 */
const devWorkflowChangeKinds = ["run", "node", "artifact", "gate"] as const;
export type DevWorkflowChangeKind = (typeof devWorkflowChangeKinds)[number];

export interface DevWorkflowChanged {
	readonly runId: string;
	readonly seq: number;
	readonly kind: string;
}

/** `events` is the replay since the client's watermark, capped at 200 with `replayTruncated`. */
export interface DevWorkflowRunSubscriptionSnapshot {
	readonly runId: string;
	readonly status: string;
	readonly queuedNodeCount: number;
	readonly runningNodeCount: number;
	readonly pendingDecisionCount: number;
	readonly blockingGateNodeRunId: string | null;
	readonly lastSeq: number;
	readonly events: readonly DevWorkflowRunEventResponse[];
	readonly replayTruncated: boolean;
}

export interface DevWorkflowRunLiveState {
	readonly connectionState: "idle" | "connecting" | "connected" | "reconnecting" | "unavailable";
	/** Painted from the snapshot before the run query lands; `undefined` until the first subscribe answers. */
	readonly status?: string;
	readonly queuedNodeCount?: number;
	readonly runningNodeCount?: number;
	readonly pendingDecisionCount?: number;
	readonly blockingGateNodeRunId?: string | null;
	/** Highest event sequence seen. Passed as `afterSeq` on every (re)subscribe. */
	readonly watermark: number;
	/** Polling cadence for the page's queries while the hub is down; `undefined` while it is live. */
	readonly pollIntervalMs?: number;
}

const emptyState: DevWorkflowRunLiveState = { connectionState: "idle", watermark: 0 };

function isChangeKind(kind: string): kind is DevWorkflowChangeKind {
	return (devWorkflowChangeKinds as readonly string[]).includes(kind);
}

/**
 * Live state for one workflow run over `development-workflows/hub`.
 *
 * Modelled on `useWorkSessionHub` and explicitly NOT on `usePreviewWorkflowHub`: the preview hook's job is dispatching
 * payload into a store, and under O10 these pings carry no content at all — the DB is the replay authority and every
 * byte the UI paints comes from a REST read. So there is no client-side mirror, only a watermark: its jobs are
 * `afterSeq` on re-subscribe and monotonic dedupe of pings.
 *
 * Node-run invalidation is keyed on the RUN, not the selected node: `[{ _id, path: { runId } }]` matches every cached
 * node-detail variant under that run by partial deep equality. That keeps the selection out of the effect's
 * dependencies, so clicking through the table does not tear down and re-establish the subscription.
 */
export function useDevWorkflowRunHub(runId: string | undefined, workItemId: string | undefined): DevWorkflowRunLiveState {
	const queryClient = useQueryClient();
	const [state, setState] = useState<DevWorkflowRunLiveState>(emptyState);

	useEffect(() => {
		if (!runId) {
			setState(emptyState);
			return;
		}

		const hub = acquireHubConnection(HUB_PATH);
		const { connection } = hub;
		let disposed = false;
		let snapshotResolved = false;
		let watermark = 0;
		let buffered: DevWorkflowChanged[] = [];

		setState({ ...emptyState, connectionState: "connecting" });

		const invalidate = (queryKey: readonly unknown[]): void => {
			queryClient.invalidateQueries({ queryKey }).catch(() => undefined);
		};

		const invalidateRun = (): void => invalidate(devWorkflowInvalidationKey(devWorkflowQueryIds.run, { runId }));
		const invalidateNodes = (): void => invalidate(devWorkflowInvalidationKey(devWorkflowQueryIds.node, { runId }));
		const invalidateWorkItem = (): void => {
			if (workItemId) {
				invalidate(devWorkflowInvalidationKey(devWorkflowQueryIds.workItem, { workItemId }));
			}
			invalidate(devWorkflowInvalidationKey(devWorkflowQueryIds.workItems));
		};

		const invalidateEveryFeed = (): void => {
			invalidateRun();
			invalidateNodes();
			invalidateWorkItem();
			invalidate(devWorkflowInvalidationKey(devWorkflowQueryIds.artifacts, { runId }));
			invalidate(devWorkflowInvalidationKey(devWorkflowQueryIds.events, { runId }));
		};

		// Both call sites gate on `change.seq > watermark` first, and `watermark` is bumped here before the next one is
		// read, so the sequence itself is the dedupe — a separate seen-set would only grow for the hook's life.
		const apply = (change: DevWorkflowChanged): void => {
			if (change.runId !== runId || change.seq <= watermark) {
				return;
			}
			watermark = change.seq;
			// Every kind moves the append-only event feed, which is why there is no `event` kind to miss.
			invalidate(devWorkflowInvalidationKey(devWorkflowQueryIds.events, { runId }));
			const kind = isChangeKind(change.kind) ? change.kind : undefined;
			switch (kind) {
				case "run":
					invalidateRun();
					invalidateWorkItem();
					break;
				case "node":
					// The node-runs ride the run payload, so a materialization (Y12) is seen here as new rows and a bumped
					// graph revision — no separate `graph` kind exists to miss.
					invalidateRun();
					invalidateNodes();
					break;
				case "gate":
					// The panel must re-arm or disappear, and the run's pendingDecisionCount moves with it.
					invalidateRun();
					invalidateNodes();
					break;
				case "artifact":
					// Deliberately a WHOLE refetch, not a cursor read: a staleness flip mutates an existing row without
					// re-stamping its sequence, so a `sinceSeq`-only reaction would never see it.
					invalidate(devWorkflowInvalidationKey(devWorkflowQueryIds.artifacts, { runId }));
					break;
				default:
					// An unknown kind (a newer server) must still refresh SOMETHING rather than be dropped silently.
					invalidateEveryFeed();
					break;
			}
			setState((current) => ({ ...current, watermark }));
		};

		const onChanged = (change: DevWorkflowChanged): void => {
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
				// Two arguments, and the watermark is re-sent on EVERY reconnect: the store serves a client that has been
				// away for days, so there is no buffer to roll over and no `replayReset` to handle.
				const snapshot = await connection.invoke<DevWorkflowRunSubscriptionSnapshot>("SubscribeRun", runId, watermark);
				if (disposed) {
					return;
				}
				const missedSomething = snapshot.replayTruncated || snapshot.events.length > 0 || snapshot.lastSeq > watermark;
				watermark = Math.max(watermark, snapshot.lastSeq);
				setState((current) => ({
					...current,
					connectionState: "connected",
					status: snapshot.status,
					queuedNodeCount: snapshot.queuedNodeCount,
					runningNodeCount: snapshot.runningNodeCount,
					pendingDecisionCount: snapshot.pendingDecisionCount,
					blockingGateNodeRunId: snapshot.blockingGateNodeRunId,
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
						pollIntervalMs: DEV_WORKFLOW_POLL_INTERVAL_MS,
					}));
				}
			}
		};

		// Polling is switched on by whatever breaks the live feed, and off ONLY by a subscribe that succeeded. A transport
		// that drops after a good subscribe used to be invisible here: the catch above never ran again, so the page kept
		// its "connected" state and painted frozen data — no alert, no refetch — until somebody reloaded it.
		const degrade = (connectionState: DevWorkflowRunLiveState["connectionState"]): void => {
			if (!disposed) {
				setState((current) => ({ ...current, connectionState, pollIntervalMs: DEV_WORKFLOW_POLL_INTERVAL_MS }));
			}
		};

		connection.on(EVENT_NAME, onChanged);
		const removeReconnecting = hub.onReconnecting(() => degrade("reconnecting"));
		const removeReconnect = hub.onReconnected(() => {
			// Re-subscribe with the CURRENT watermark, so everything the run did while the transport was down arrives as
			// replay instead of being skipped. `subscribe` clears the poll interval once the snapshot lands.
			subscribe(true).catch(() => undefined);
		});
		// The retry policy gave up. Nothing will re-announce this, so the poll stays on for the page's lifetime.
		const removeClosed = hub.onClosed(() => degrade("unavailable"));
		hub.whenStarted.then(() => subscribe(false)).catch(() => undefined);

		return () => {
			disposed = true;
			connection.off(EVENT_NAME, onChanged);
			removeReconnecting();
			removeClosed();
			removeReconnect();
			if (connection.state === HubConnectionState.Connected) {
				connection.invoke("UnsubscribeRun", runId).catch(() => undefined);
			}
			hub.release();
		};
	}, [queryClient, runId, workItemId]);

	return state;
}
