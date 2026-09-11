import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { isExternalAppBusy, toExternalAppStatus } from "@/features/externalApps/models/ExternalAppModels";
import { externalAppInvalidationKey, externalAppQueryIds } from "@/features/externalApps/queries/useExternalApps";

const HUB_PATH = "external-apps/hub";
const CHANGED_EVENT = "externalAppChanged";
const PULL_PROGRESS_EVENT = "externalAppPullProgress";
/** The fallback cadence while the hub is down: fast enough to feel live, slow enough not to hammer a broken hub. */
export const EXTERNAL_APP_POLL_INTERVAL_MS = 3_000;

/**
 * The change ping. It carries `kind` lowerCamelCase — and this hook NEVER reads it: the ping is a notification, the
 * REST reads are the authority, so a kind this build has never heard of still advances the watermark and still
 * refreshes every feed. `status` is kept only to label the degraded-live case.
 */
interface ExternalAppChanged {
	readonly instanceId: string;
	readonly sequence: number;
	readonly status?: string;
}

/** The one hub message that IS its payload: no REST read can report a download that is still running. */
export interface ExternalAppPullProgress {
	readonly instanceId: string;
	readonly service: string;
	readonly layerCount: number;
	readonly completedLayers: number;
	readonly bytes: number;
}

/** `events` is the replay since the client's watermark, capped at 200 with `replayTruncated`. */
export interface ExternalAppSubscriptionSnapshot {
	readonly instanceId: string;
	readonly status: string;
	readonly desiredState: string;
	readonly failureCategory: string | null;
	readonly lastSequence: number;
	readonly events: readonly unknown[];
	readonly replayTruncated: boolean;
}

export interface ExternalAppLiveState {
	readonly connectionState: "idle" | "connecting" | "connected" | "reconnecting" | "unavailable";
	readonly status?: string;
	/** The only way to tell "stopped because the operator asked" from "stopped and should be running". */
	readonly desiredState?: string;
	readonly failureCategory?: string | null;
	/** Highest sequence seen. Re-sent as `afterSequence` on every (re)subscribe. */
	readonly watermark: number;
	/** Polling cadence for the page's queries while the hub is down; `undefined` while it is live. */
	readonly pollIntervalMs?: number;
	/** Per service, and cleared on every new subscribe — a stale bar is worse than none. */
	readonly pullProgress: Readonly<Record<string, ExternalAppPullProgress>>;
}

const emptyState: ExternalAppLiveState = { connectionState: "idle", watermark: 0, pullProgress: {} };

/**
 * Live state for one installed application over `external-apps/hub`.
 *
 * The change pings carry no state worth painting, so there is no client-side mirror: the watermark's jobs are
 * `afterSequence` on re-subscribe and monotonic dedupe. Pull progress is the exception — it exists nowhere else — and
 * it lands in local state only. Invalidating a query per layer would turn a multi-gigabyte pull into a request storm.
 */
export function useExternalAppHub(instanceId: string | undefined): ExternalAppLiveState {
	const queryClient = useQueryClient();
	const [state, setState] = useState<ExternalAppLiveState>(emptyState);

	useEffect(() => {
		if (!instanceId) {
			setState(emptyState);
			return;
		}

		const hub = acquireHubConnection(HUB_PATH);
		const { connection } = hub;
		let disposed = false;
		let snapshotResolved = false;
		let watermark = 0;
		let buffered: ExternalAppChanged[] = [];

		setState({ ...emptyState, connectionState: "connecting" });

		const invalidate = (queryKey: readonly unknown[]): void => {
			queryClient.invalidateQueries({ queryKey }).catch(() => undefined);
		};

		const invalidateEveryFeed = (): void => {
			invalidate(externalAppInvalidationKey(externalAppQueryIds.instance, { instanceId }));
			invalidate(externalAppInvalidationKey(externalAppQueryIds.instances));
			invalidate(externalAppInvalidationKey(externalAppQueryIds.events, { instanceId }));
			// The logs tab reads on demand and never polls, so a ping is the only thing that can refresh it in place.
			invalidate(externalAppInvalidationKey(externalAppQueryIds.logs, { instanceId }));
		};

		const apply = (change: ExternalAppChanged): void => {
			if (change.instanceId !== instanceId || change.sequence <= watermark) {
				return;
			}
			watermark = change.sequence;
			invalidateEveryFeed();
			// A settled status means an install or an uninstall finished, which changes what the runtime is running.
			if (change.status && !isExternalAppBusy(toExternalAppStatus(change.status))) {
				invalidate(externalAppInvalidationKey(externalAppQueryIds.runtime));
			}
			setState((current) => ({ ...current, status: change.status ?? current.status, watermark }));
		};

		const onChanged = (change: ExternalAppChanged): void => {
			if (!snapshotResolved) {
				buffered.push(change);
				return;
			}
			apply(change);
		};

		const onPullProgress = (progress: ExternalAppPullProgress): void => {
			if (progress.instanceId !== instanceId) {
				return;
			}
			setState((current) => ({
				...current,
				pullProgress: { ...current.pullProgress, [progress.service]: progress },
			}));
		};

		// Polling is switched on by whatever breaks the live feed, and off ONLY by a subscribe that succeeded: a
		// transport that drops after a good subscribe would otherwise keep a "connected" page painting frozen rows.
		const degrade = (connectionState: ExternalAppLiveState["connectionState"]): void => {
			if (!disposed) {
				setState((current) => ({ ...current, connectionState, pollIntervalMs: EXTERNAL_APP_POLL_INTERVAL_MS }));
			}
		};

		const subscribe = async (reconnecting: boolean): Promise<void> => {
			if (disposed) {
				return;
			}
			// A transport that never came up is the SAME degraded case as one that dropped later. Returning quietly
			// here left the hook painting "connecting" for the page's lifetime with no poll behind it, so a failed
			// initial negotiate showed a frozen instance and no sign that anything was wrong.
			if (connection.state !== HubConnectionState.Connected) {
				degrade("unavailable");
				return;
			}
			snapshotResolved = false;
			buffered = [];
			setState((current) => ({
				...current,
				connectionState: reconnecting ? "reconnecting" : "connecting",
				pullProgress: {},
			}));
			try {
				// Two arguments, and the watermark is re-sent on EVERY reconnect, so what happened while the transport
				// was down arrives as replay instead of being skipped.
				const snapshot = await connection.invoke<ExternalAppSubscriptionSnapshot>("Subscribe", instanceId, watermark);
				if (disposed) {
					return;
				}
				const missedSomething = snapshot.replayTruncated || snapshot.events.length > 0 || snapshot.lastSequence > watermark;
				watermark = Math.max(watermark, snapshot.lastSequence);
				setState((current) => ({
					...current,
					connectionState: "connected",
					status: snapshot.status,
					desiredState: snapshot.desiredState,
					failureCategory: snapshot.failureCategory,
					watermark,
					pollIntervalMs: undefined,
				}));
				if (missedSomething) {
					invalidateEveryFeed();
				}
				snapshotResolved = true;
				for (const change of buffered.toSorted((left, right) => left.sequence - right.sequence)) {
					apply(change);
				}
				buffered = [];
			} catch {
				if (!disposed) {
					// Never a blocking error: the page falls back to polling and keeps rendering last-good state.
					setState((current) => ({
						...current,
						connectionState: "unavailable",
						pollIntervalMs: EXTERNAL_APP_POLL_INTERVAL_MS,
					}));
				}
			}
		};

		connection.on(CHANGED_EVENT, onChanged);
		connection.on(PULL_PROGRESS_EVENT, onPullProgress);
		const removeReconnecting = hub.onReconnecting(() => degrade("reconnecting"));
		const removeReconnect = hub.onReconnected(() => {
			subscribe(true).catch(() => undefined);
		});
		// The retry policy gave up. Nothing will re-announce this, so the poll stays on for the page's lifetime.
		const removeClosed = hub.onClosed(() => degrade("unavailable"));
		// `whenStarted` settles on success AND on failure, and the manager swallows the start error — so the state is
		// re-checked inside `subscribe`, and a rejection here degrades rather than being dropped on the floor.
		hub.whenStarted.then(() => subscribe(false)).catch(() => degrade("unavailable"));

		return () => {
			disposed = true;
			connection.off(CHANGED_EVENT, onChanged);
			connection.off(PULL_PROGRESS_EVENT, onPullProgress);
			removeReconnecting();
			removeClosed();
			removeReconnect();
			if (connection.state === HubConnectionState.Connected) {
				connection.invoke("Unsubscribe", instanceId).catch(() => undefined);
			}
			hub.release();
		};
	}, [queryClient, instanceId]);

	return state;
}
