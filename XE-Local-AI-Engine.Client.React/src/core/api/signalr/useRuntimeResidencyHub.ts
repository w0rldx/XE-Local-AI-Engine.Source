import { HubConnectionState } from "@microsoft/signalr";
import { type QueryKey, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { z } from "zod";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";

/* eslint-disable react-doctor/effect-needs-cleanup -- The cleanup does tear down: it offs the registered handler and releases the shared lease; the rule only recognises teardown of a connection created inline in the effect. */

// Server-pushed "the resident set changed" tick: a llama.cpp model warmed, evicted or ejected, or an image /
// transcription process started or stopped. The tick carries no rows; each subscribing query invalidates itself and
// refetches its canonical list over REST.
const RUNTIME_RESIDENCY_CHANGED = "runtimeResidency.changed";

// Untrusted boundary data: a payload that fails the schema is dropped, as the other hub hooks do.
const runtimeResidencyChangedSchema = z.object({ sequence: z.number() });

export interface RuntimeResidencyHubState {
	/** True while the hub is connected; false while it is reconnecting, closed or not started yet, so callers poll. */
	readonly isLive: boolean;
}

/**
 * Subscribes one query to the runtime-residency hub for the lifetime of the mounting component: a tick, the start and
 * every reconnect invalidate `queryKey` only. `enabled` follows the query's own: signed out the negotiate would fail.
 * Several subscribers share one refcounted connection; each keeps a per-mount handler and a StrictMode-safe deferred
 * release, as the other local hubs do.
 */
export function useRuntimeResidencyHub(enabled: boolean, queryKey: QueryKey): RuntimeResidencyHubState {
	const queryClient = useQueryClient();
	const [isLive, setIsLive] = useState(false);
	// Generated keys are fresh arrays every render; the effect keys on their content so it does not reconnect per render.
	const queryKeyHash = JSON.stringify(queryKey);

	useEffect(() => {
		if (!enabled) {
			return undefined;
		}

		const hub = acquireHubConnection("model-fit/residency/hub");
		const { connection } = hub;
		const invalidate = (): void => {
			queryClient.invalidateQueries({ queryKey: JSON.parse(queryKeyHash) as QueryKey }).catch(() => undefined);
		};

		const handleChanged = (payload: unknown): void => {
			if (runtimeResidencyChangedSchema.safeParse(payload).success) {
				invalidate();
			}
		};
		connection.on(RUNTIME_RESIDENCY_CHANGED, handleChanged);

		let disposed = false;
		const degrade = (): void => {
			if (!disposed) {
				setIsLive(false);
			}
		};
		// The hub broadcasts with no replay, so whatever changed before this (re)connect is read once over REST.
		const resume = (): void => {
			if (disposed) {
				return;
			}
			if (connection.state !== HubConnectionState.Connected) {
				degrade();
				return;
			}
			invalidate();
			setIsLive(true);
		};
		// release() drops these registrations, so the cleanup needs no unregister calls of its own.
		hub.onReconnecting(degrade);
		hub.onReconnected(resume);
		hub.onClosed(degrade);
		hub.whenStarted.then(resume).catch(degrade);

		return () => {
			disposed = true;
			// A later re-enable starts from the fallback cadence until its own connection reports in.
			setIsLive(false);
			connection.off(RUNTIME_RESIDENCY_CHANGED, handleChanged);
			// Release the shared lease: the manager stops the connection only after the LAST subscriber releases, and only
			// once the start promise settles (so cleanup never aborts an in-flight negotiation under StrictMode).
			hub.release();
		};
	}, [enabled, queryClient, queryKeyHash]);

	return { isLive };
}
