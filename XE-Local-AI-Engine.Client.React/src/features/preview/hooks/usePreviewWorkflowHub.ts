import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
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
// The backend publishes every event to the run's SignalR group (PreviewWorkflowHub.RunGroup), so a connection
// receives NOTHING until it has joined that group via the hub's Subscribe(runId) method. This hook therefore mirrors
// the PreviewRunStore's set of active runIds onto the connection: when a runId becomes active it invokes
// `Subscribe(runId)`, and when it leaves it invokes `Unsubscribe(runId)`. Invokes are queued behind the start promise
// (so they never fire on an unconnected hub) and re-applied on reconnect (a transient drop loses group membership).
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

		// The runIds this connection has joined a group for. Diffed against the store's active set so each runId is
		// Subscribe'd / Unsubscribe'd exactly once. Lives across reconnects so a reconnect can re-join every active run.
		const subscribedRunIds = new Set<string>();

		// Best-effort group join/leave. Only invoked while the hub is Connected (the diff reconcile guards on state and
		// onreconnected re-runs after a drop), so the connection is up by the time this runs.
		const joinRunGroup = (runId: string): void => {
			connection.invoke("Subscribe", runId).catch((error: unknown) => {
				console.warn("preview workflow hub failed to subscribe to run", runId, error);
			});
		};

		const leaveRunGroup = (runId: string): void => {
			connection.invoke("Unsubscribe", runId).catch((error: unknown) => {
				console.warn("preview workflow hub failed to unsubscribe from run", runId, error);
			});
		};

		// Reconcile the connection's group membership to the desired active-runId set. New runIds are joined, removed
		// runIds are left. No-op unless the connection is Connected (a desired set computed while disconnected is
		// applied wholesale by onreconnected / the post-start reconcile).
		const reconcileSubscriptions = (desired: ReadonlySet<string>): void => {
			if (connection.state !== HubConnectionState.Connected) {
				return;
			}
			for (const runId of desired) {
				if (!subscribedRunIds.has(runId)) {
					subscribedRunIds.add(runId);
					joinRunGroup(runId);
				}
			}
			for (const runId of [...subscribedRunIds]) {
				if (!desired.has(runId)) {
					subscribedRunIds.delete(runId);
					leaveRunGroup(runId);
				}
			}
		};

		const desiredRunIds = (): Set<string> => new Set(Object.keys(usePreviewRunStore.getState().runs));

		// React to the store's active-run set changing (registerRun on Execute / reset on unmount) — fires only when
		// the keys change, then reconciles group membership.
		const unsubscribeStore = usePreviewRunStore.subscribe((state, previous) => {
			const nextKeys = Object.keys(state.runs);
			const prevKeys = Object.keys(previous.runs);
			if (nextKeys.length === prevKeys.length && nextKeys.every((key) => key in previous.runs)) {
				return;
			}
			reconcileSubscriptions(new Set(nextKeys));
		});

		let disposed = false;
		const startPromise = connection.start().then(
			() => {
				if (disposed) {
					return;
				}
				// On (re)connect, group membership is empty on the server — forget what we thought we'd joined and
				// re-apply the current desired set so a run that was active before/at connect is subscribed.
				subscribedRunIds.clear();
				reconcileSubscriptions(desiredRunIds());
			},
			(error: unknown) => {
				// A start aborted by our own cleanup (StrictMode double-invoke / fast remount) is not a real failure.
				if (disposed) {
					return;
				}
				// A hub that cannot connect must not break the page — the canvas still edits/saves. The hub is best-effort
				// live run output, so a connection failure is surfaced only to the console.
				console.warn("preview workflow hub failed to start", error);
			},
		);

		// After a transient drop + automatic reconnect the server has dropped all group memberships, so re-join every
		// currently-active run.
		connection.onreconnected(() => {
			subscribedRunIds.clear();
			reconcileSubscriptions(desiredRunIds());
		});

		return () => {
			disposed = true;
			unsubscribeStore();
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
