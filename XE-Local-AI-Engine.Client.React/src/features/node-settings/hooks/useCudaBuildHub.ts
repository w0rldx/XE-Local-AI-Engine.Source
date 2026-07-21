import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useState } from "react";
import { z } from "zod";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { localRuntimeInvalidationKey, localRuntimeQueryIds } from "@/features/node-settings/queries/useLocalRuntime";

// Realtime push for the in-app CUDA build progress. Connects to the CUDA-build SignalR hub for the lifetime of the
// mounting card and accumulates the streamed phase + log deltas + terminal/error into UI-only React state (never cached
// or persisted — the authoritative snapshot lives in the GET status query, which this hook seeds nothing into).
//
// The server pushes the `cudaBuild.statusChanged` method with a delta payload: the latest `phase`, only the NEW log
// lines since the last push (`appendedLogLines`), whether the build reached a terminal state, and a sanitized error.
// On a terminal event the persisted status + runtime caches are invalidated so the card re-reads the final state.
//
// Connection lifetime copies the race-safe pattern from the other local hubs (scheduler/preview): a hub that fails to
// connect must never break the card; cleanup defers connection.stop() until the start promise settles so a StrictMode
// double-invoke / fast remount cannot abort an in-flight negotiation.

// The client method the backend invokes to push a build status delta.
const CUDA_BUILD_STATUS_CHANGED = "cudaBuild.statusChanged";

// Upper bound on retained live log lines. A CUDA from-source build streams tens of thousands of compiler lines; only
// the tail is ever rendered, so keep a bounded ring like the other diagnostics buffers (breadcrumbs/rrweb) instead of
// growing per delta for the whole build.
const MAX_LOG_LINES = 2000;

// Wire payload (camelCase). Untrusted boundary data — validated with zod before it touches UI state; a payload that
// fails the schema is dropped (best-effort live output; the GET status query still serves the canonical state).
const cudaBuildHubEventSchema = z.object({
	phase: z.string(),
	appendedLogLines: z.array(z.string()),
	terminal: z.boolean(),
	sanitizedError: z.string().nullable(),
});

// Live build state surfaced to the card. `logLines` holds the most recent MAX_LOG_LINES streamed lines; `phase`,
// `terminal`, and `error` reflect the most recent event. All UI-only.
export interface CudaBuildLiveState {
	readonly phase: string | null;
	readonly logLines: readonly string[];
	readonly terminal: boolean;
	readonly error: string | null;
	/** Clears the accumulated live state — call before kicking off a fresh build so a prior run's log does not bleed in. */
	readonly reset: () => void;
}

const emptyLiveState = {
	phase: null as string | null,
	logLines: [] as readonly string[],
	terminal: false,
	error: null as string | null,
};

// Subscribes to the CUDA-build hub for the card's lifetime and accumulates streamed deltas into UI-only state. The hub
// only emits while a build runs, so an always-mounted subscription is cheap. Returns the live phase/log/terminal/error
// plus a `reset` to clear the accumulator between builds.
export function useCudaBuildHub(): CudaBuildLiveState {
	const queryClient = useQueryClient();
	const [state, setState] = useState(emptyLiveState);

	const reset = useCallback(() => setState(emptyLiveState), []);

	useEffect(() => {
		// Shared refcounted connection: reused across mounts so re-opening the node-settings card does not pay a fresh
		// negotiate + WebSocket upgrade. The status handler below stays per-mount so this subscriber coexists with any
		// other subscriber to the same hub.
		const hub = acquireHubConnection("model-fit/llamacpp/cuda-build/hub");
		const { connection } = hub;

		const handleStatusChanged = (payload: unknown): void => {
			const parsed = cudaBuildHubEventSchema.safeParse(payload);
			if (!parsed.success) {
				return;
			}
			const event = parsed.data;
			setState((current) => {
				let logLines = current.logLines;
				if (event.appendedLogLines.length > 0) {
					const appended = [...current.logLines, ...event.appendedLogLines];
					// Keep only the tail so a long build cannot grow this UI state unboundedly.
					logLines = appended.length > MAX_LOG_LINES ? appended.slice(appended.length - MAX_LOG_LINES) : appended;
				}
				return {
					phase: event.phase,
					logLines,
					terminal: event.terminal,
					error: event.sanitizedError,
				};
			});
			// On a terminal event the persisted snapshot changed — re-read the canonical status + runtime so the card flips
			// out of the building state and reflects an adopted managed build.
			if (event.terminal) {
				queryClient
					.invalidateQueries({ queryKey: localRuntimeInvalidationKey(localRuntimeQueryIds.cudaBuildStatus) })
					.catch(() => undefined);
				queryClient
					.invalidateQueries({ queryKey: localRuntimeInvalidationKey(localRuntimeQueryIds.llamaCppRuntime) })
					.catch(() => undefined);
			}
		};

		connection.on(CUDA_BUILD_STATUS_CHANGED, handleStatusChanged);

		return () => {
			connection.off(CUDA_BUILD_STATUS_CHANGED, handleStatusChanged);
			// Release the shared lease: the manager stops the connection only after the LAST subscriber releases, and only
			// once the start promise settles (so cleanup never aborts an in-flight negotiation under StrictMode / fast remounts).
			hub.release();
		};
	}, [queryClient]);

	return { ...state, reset };
}
