import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import type { GraphWorkflowRunEventResponse } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { graphWorkflowInvalidationKey, graphWorkflowQueryIds } from "@/features/graphWorkflows/queries/useGraphWorkflows";

const HUB_PATH = "graph-workflows/hub";
const EVENT_NAME = "graphWorkflowChanged";
/** The dev-workflow fallback cadence, reused: fast enough to feel live, slow enough not to hammer a broken hub. */
export const GRAPH_WORKFLOW_POLL_INTERVAL_MS = 3_000;

/**
 * Exactly three kinds, **LOWERCASE on the wire** — `GraphWorkflowChanged` carries the kind in lower case, so a casing
 * slip here makes every invalidation a silent no-op rather than an error. The literals are asserted in
 * `useGraphWorkflowRunHub.test.tsx` for exactly that reason.
 *
 * There is no `event` kind: every kind moves the append-only event feed, so the feed is invalidated unconditionally.
 */
const graphWorkflowChangeKinds = ["run", "node", "gate"] as const;
export type GraphWorkflowChangeKind = (typeof graphWorkflowChangeKinds)[number];

export interface GraphWorkflowChanged {
	readonly runId: string;
	readonly seq: number;
	readonly kind: string;
}

/** `events` is the replay since the client's watermark, capped at `EventReplayLimit` with `replayTruncated`. */
export interface GraphWorkflowRunSubscriptionSnapshot {
	readonly runId: string;
	readonly status: string;
	readonly queuedNodeCount: number;
	readonly runningNodeCount: number;
	readonly pendingDecisionCount: number;
	readonly lastSeq: number;
	readonly events: readonly GraphWorkflowRunEventResponse[];
	readonly replayTruncated: boolean;
}

export interface GraphWorkflowRunLiveState {
	readonly connectionState: "idle" | "connecting" | "connected" | "reconnecting" | "unavailable";
	/** Painted from the snapshot before the run query lands; `undefined` until the first subscribe answers. */
	readonly status?: string;
	readonly queuedNodeCount?: number;
	readonly runningNodeCount?: number;
	readonly pendingDecisionCount?: number;
	/** Highest event sequence seen. Passed as `afterSeq` on every (re)subscribe. */
	readonly watermark: number;
	/** Polling cadence for the page's queries while the hub is down; `undefined` while it is live. */
	readonly pollIntervalMs?: number;
}

const emptyState: GraphWorkflowRunLiveState = { connectionState: "idle", watermark: 0 };

function isChangeKind(kind: string): kind is GraphWorkflowChangeKind {
	return (graphWorkflowChangeKinds as readonly string[]).includes(kind);
}

/**
 * Live state for one graph workflow run over `graph-workflows/hub`.
 *
 * The pings carry no content at all — the store is the replay authority and every byte the UI paints comes from a REST
 * read. So there is no client-side mirror, only a watermark: its jobs are `afterSeq` on re-subscribe and monotonic
 * dedupe of pings.
 *
 * Node-run invalidation is keyed on the RUN, not the selected node: `[{ _id, path: { runId } }]` matches every cached
 * node-detail variant under that run by partial deep equality. That keeps the selection out of the effect's
 * dependencies, so clicking through the node table does not tear down and re-establish the subscription.
 */
export function useGraphWorkflowRunHub(runId: string | undefined): GraphWorkflowRunLiveState {
	const queryClient = useQueryClient();
	const [state, setState] = useState<GraphWorkflowRunLiveState>(emptyState);

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
		let buffered: GraphWorkflowChanged[] = [];

		setState({ ...emptyState, connectionState: "connecting" });

		const invalidate = (queryKey: readonly unknown[]): void => {
			queryClient.invalidateQueries({ queryKey }).catch(() => undefined);
		};

		const invalidateRun = (): void => invalidate(graphWorkflowInvalidationKey(graphWorkflowQueryIds.run, { runId }));
		const invalidateNodes = (): void => invalidate(graphWorkflowInvalidationKey(graphWorkflowQueryIds.node, { runId }));
		const invalidateRunList = (): void => invalidate(graphWorkflowInvalidationKey(graphWorkflowQueryIds.runs));

		const invalidateEveryFeed = (): void => {
			invalidateRun();
			invalidateNodes();
			invalidateRunList();
			invalidate(graphWorkflowInvalidationKey(graphWorkflowQueryIds.events, { runId }));
		};

		// Both call sites gate on `change.seq > watermark` first, and `watermark` is bumped here before the next one is
		// read, so the sequence itself is the dedupe — a separate seen-set would only grow for the hook's life.
		const apply = (change: GraphWorkflowChanged): void => {
			if (change.runId !== runId || change.seq <= watermark) {
				return;
			}
			watermark = change.seq;
			// Every kind moves the append-only event feed, which is why there is no `event` kind to miss.
			invalidate(graphWorkflowInvalidationKey(graphWorkflowQueryIds.events, { runId }));
			const kind = isChangeKind(change.kind) ? change.kind : undefined;
			switch (kind) {
				case "run":
					// The list shows this run's status, so a lifecycle move belongs there as much as on the run itself.
					invalidateRun();
					invalidateRunList();
					break;
				case "node":
					// The node runs ride the run payload, so a status move is seen there as well as on the open node detail.
					invalidateRun();
					invalidateNodes();
					break;
				case "gate":
					// The decision panel must re-arm or disappear, and a parked run reads `WaitingForApproval` in the list.
					invalidateRun();
					invalidateNodes();
					invalidateRunList();
					break;
				default:
					// An unknown kind (a newer server) must still refresh SOMETHING rather than be dropped silently.
					invalidateEveryFeed();
					break;
			}
			setState((current) => ({ ...current, watermark }));
		};

		const onChanged = (change: GraphWorkflowChanged): void => {
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
				const snapshot = await connection.invoke<GraphWorkflowRunSubscriptionSnapshot>("SubscribeRun", runId, watermark);
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
						pollIntervalMs: GRAPH_WORKFLOW_POLL_INTERVAL_MS,
					}));
				}
			}
		};

		// Polling is switched on by whatever breaks the live feed, and off ONLY by a subscribe that succeeded. A transport
		// that drops after a good subscribe is otherwise invisible: the catch above never runs again, so the page keeps
		// its "connected" state and paints frozen data — no alert, no refetch — until somebody reloads it.
		const degrade = (connectionState: GraphWorkflowRunLiveState["connectionState"]): void => {
			if (!disposed) {
				setState((current) => ({ ...current, connectionState, pollIntervalMs: GRAPH_WORKFLOW_POLL_INTERVAL_MS }));
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
	}, [queryClient, runId]);

	return state;
}
