import { useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { z } from "zod";

import { getRuntimeAcquisitionStatusOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import {
	keepLatestAcquisitionStatus,
	type RuntimeAcquisitionStatus,
	useRuntimeAcquisitionStatus,
} from "@/features/node-settings/queries/useLocalRuntime";

// Live view of the host's llama.cpp runtime acquisition (GPU probe → download → verify → extract), for the global
// first-run banner. Unlike the other local hubs this one does NOT keep its own React state: it hydrates from the
// read endpoint on mount and reconciles hub pushes into that SAME query cache entry, because hydrate and push are two
// views of one server-side status and the banner must render exactly one of them.
//
// The late-join case is the whole point of the hydrate leg: acquisition starts within seconds of host boot, usually
// before the client has authenticated and opened this connection, so a push-only subscription would miss the entire
// download on precisely the slow first run the banner exists to explain.
//
// Connection lifetime copies the race-safe pattern from the other local hubs (cuda-build/source-build): a shared
// refcounted connection, a per-mount handler so this subscriber coexists with any other, and a deferred release so a
// StrictMode double-invoke cannot abort an in-flight negotiation.

// The client method the backend invokes to push an acquisition status.
const RUNTIME_ACQUISITION_STATUS_CHANGED = "runtimeAcquisition.statusChanged";

// Wire payload (camelCase), mirroring the hydrate DTO field for field. Untrusted boundary data — validated with zod
// before it reaches the cache; a payload that fails the schema is dropped rather than guessed at, exactly as the other
// hub hooks do (the hydrate query still serves the canonical state, and the next push re-synchronizes).
const runtimeAcquisitionStatusSchema = z.object({
	sequence: z.number(),
	phase: z.string(),
	variant: z.string().nullable().optional(),
	tag: z.string().nullable().optional(),
	completedBytes: z.number().nullable().optional(),
	totalBytes: z.number().nullable().optional(),
	stepIndex: z.number().int(),
	stepCount: z.number().int(),
	sanitizedError: z.string().nullable().optional(),
});

/**
 * Subscribes to the runtime-acquisition hub and returns the reconciled status — the newest of the hydrate snapshot and
 * every push, decided by {@link keepLatestAcquisitionStatus}'s monotonic-sequence rule. `undefined` until the first of
 * the two lands. `enabled` gates BOTH legs on authentication: the GET would 401 pre-login and the hub would fail to
 * negotiate.
 */
export function useRuntimeAcquisitionHub(enabled = true): RuntimeAcquisitionStatus | undefined {
	const queryClient = useQueryClient();
	const statusQuery = useRuntimeAcquisitionStatus(enabled);

	useEffect(() => {
		if (!enabled) {
			return undefined;
		}

		// Shared refcounted connection, per-mount handler — see the header note.
		const hub = acquireHubConnection("model-fit/llamacpp/acquisition/hub");
		const { connection } = hub;
		const queryKey = getRuntimeAcquisitionStatusOptions().queryKey;

		const handleStatusChanged = (payload: unknown): void => {
			const parsed = runtimeAcquisitionStatusSchema.safeParse(payload);
			if (!parsed.success) {
				return;
			}
			// The same guard the hydrate write goes through (as this query's `structuralSharing`). Applying it here too
			// keeps the push leg correct on its own terms rather than relying on the query being mounted, and the rule is
			// a max — applying it twice changes nothing.
			queryClient.setQueryData(queryKey, (current: RuntimeAcquisitionStatus | undefined) =>
				keepLatestAcquisitionStatus(current, parsed.data),
			);
		};

		connection.on(RUNTIME_ACQUISITION_STATUS_CHANGED, handleStatusChanged);
		// A reconnect means pushes were missed while the socket was down; re-read the endpoint so a status that reached a
		// terminal phase during the gap is not left mid-download on screen. The sequence guard makes this re-read safe
		// even if it races the first push after reconnect.
		const unregisterReconnected = hub.onReconnected(() => {
			queryClient.invalidateQueries({ queryKey }).catch(() => undefined);
		});

		return () => {
			unregisterReconnected();
			connection.off(RUNTIME_ACQUISITION_STATUS_CHANGED, handleStatusChanged);
			// Release the shared lease: the manager stops the connection only after the LAST subscriber releases, and only
			// once the start promise settles (so cleanup never aborts an in-flight negotiation under StrictMode).
			hub.release();
		};
	}, [enabled, queryClient]);

	return statusQuery.data;
}
