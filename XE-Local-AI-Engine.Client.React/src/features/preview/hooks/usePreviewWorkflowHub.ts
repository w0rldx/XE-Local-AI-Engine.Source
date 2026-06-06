import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { useEffect } from "react";

import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import {
	previewNodeEventNames,
	previewNodeEventSchema,
	previewRunEventNames,
	previewRunEventSchema,
} from "@/features/preview/models/PreviewWorkflowModels";
import { usePreviewRunStore } from "@/features/preview/stores/PreviewRunStore";

// Realtime push for the Open Canvas (Preview) run output. Connects to the preview SignalR hub for the lifetime of
// the mounting component and forwards every node/run event into the PreviewRunStore, which applies it ONLY if the
// event's runId is one this tab registered (registerRun) — the foreign-run guard (decision #3 + MEDIUM-2) lives
// in the store, so this hook stays a thin transport that validates the wire payload and dispatches by event name.
//
// Connection lifetime copies the race-safe pattern from useModelFitSchedulerEvents: a hub that fails to connect
// must never break the page (the canvas still works for editing); cleanup defers connection.stop() until the
// start promise settles so a StrictMode double-invoke / fast remount cannot abort an in-flight negotiation (the
// "stopped during negotiation" race that otherwise leaves the hub permanently disconnected). There is no `t`
// dependency here (no toasts), so the only effect dependency is the stable store-actions reference.

export function usePreviewWorkflowHub(): void {
	// Stable across renders (zustand actions object is created once), so it is a safe single effect dependency and
	// the connection is built exactly once per mount.
	const actions = usePreviewRunStore((state) => state.actions);

	useEffect(() => {
		const connection = new HubConnectionBuilder()
			.withUrl(buildLocalApiUrl("preview/hub"), {
				accessTokenFactory: () => useNodeAuthStore.getState().accessToken ?? "",
			})
			// Persistent channel for the page lifetime — auto-reconnect after a transient drop so live run output is
			// not silently lost for the rest of the session. Matches the other local hubs.
			.withAutomaticReconnect()
			.configureLogging(LogLevel.Warning)
			.build();

		// Node-scoped events: validate the wire payload (untrusted) then dispatch to the store. A payload that fails
		// the schema is dropped (best-effort live output; the canvas keeps working).
		const nodeHandlers = previewNodeEventNames.map((eventName) => {
			const handler = (payload: unknown): void => {
				const parsed = previewNodeEventSchema.safeParse(payload);
				if (parsed.success) {
					actions.applyNodeEvent(parsed.data);
				}
			};
			connection.on(eventName, handler);
			return [eventName, handler] as const;
		});

		// Run-lifecycle events: same validate-then-dispatch path.
		const runHandlers = previewRunEventNames.map((eventName) => {
			const handler = (payload: unknown): void => {
				const parsed = previewRunEventSchema.safeParse(payload);
				if (parsed.success) {
					actions.applyRunEvent(parsed.data);
				}
			};
			connection.on(eventName, handler);
			return [eventName, handler] as const;
		});

		let disposed = false;
		const startPromise = connection.start().then(undefined, (error: unknown) => {
			// A start aborted by our own cleanup (StrictMode double-invoke / fast remount) is not a real failure.
			if (disposed) {
				return;
			}
			// A hub that cannot connect must not break the page — the canvas still edits/saves. The hub is best-effort
			// live run output, so a connection failure is surfaced only to the console.
			console.warn("preview workflow hub failed to start", error);
		});

		return () => {
			disposed = true;
			for (const [eventName, handler] of [...nodeHandlers, ...runHandlers]) {
				connection.off(eventName, handler);
			}
			// Stop only AFTER start settles so cleanup never aborts an in-flight negotiation (the "stopped during
			// negotiation" race that left the hub permanently disconnected under StrictMode / fast remounts).
			startPromise.finally(() => {
				connection.stop().catch((error: unknown) => {
					console.warn("preview workflow hub failed to stop", error);
				});
			});
		};
	}, [actions]);
}
