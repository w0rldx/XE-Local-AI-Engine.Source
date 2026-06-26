import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";

import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { notifySchedulerRunEvent } from "@/features/scheduler/notifications/SchedulerRunNotifications";
import { schedulerInvalidationKey, schedulerQueryIds } from "@/features/scheduler/queries/useScheduler";

// The data layer is the generated hey-api TanStack query layer, whose query keys are single-element arrays
// `[{ _id: "<operationId>", ... }]`. Invalidating with the `_id` partial object (via schedulerInvalidationKey)
// matches every cached variant of that endpoint (TanStack partial-object matching) — the realtime analogue of the
// old prefix invalidation.

// Server-pushed scheduler events. The hub is notification-only: a push tells the client that state changed but
// carries no authoritative payload to render directly, so each handler simply invalidates the matching TanStack
// Query cache and lets the query refetch the canonical state. Event names are the string method names the
// backend invokes on the client.
const JOB_DEFINITION_CHANGED = "scheduler.jobDefinitionChanged";
const RUN_EVENTS = [
	"scheduler.runStarted",
	"scheduler.runCompleted",
	"scheduler.runFailed",
	"scheduler.runCancelled",
	"scheduler.runProgress",
] as const;

// Subscribes to the scheduler SignalR hub for the lifetime of the mounting component. On a job-definition change
// the jobs list is invalidated; on any run event the run history + per-run detail are invalidated. The hub is a
// notification channel only — authoritative state is always refetched via TanStack Query. Connection failures
// are tolerated silently (logged to console.warn) so a flaky hub never breaks the page; the queries still serve
// their last good data and the user can refetch by re-navigating.
export function useSchedulerHub(): void {
	const queryClient = useQueryClient();
	// `t` is read through a ref so a language switch (which hands back a new `t`) re-localizes future toasts WITHOUT
	// re-running the effect — re-running would tear down and rebuild the live hub connection. Kept current every render.
	const { t } = useTranslation();
	const tRef = useRef(t);
	tRef.current = t;

	useEffect(() => {
		const connection = new HubConnectionBuilder()
			.withUrl(buildLocalApiUrl("scheduler/hub"), {
				accessTokenFactory: () => useNodeAuthStore.getState().accessToken ?? "",
			})
			// Persistent notification channel (mounted for the page lifetime), so auto-reconnect after a transient drop —
			// otherwise live invalidation is silently lost for the rest of the session. Matches the chat hub precedent.
			.withAutomaticReconnect()
			.configureLogging(LogLevel.Warning)
			.build();

		const invalidateJobs = (): void => {
			queryClient.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.listJobs) }).catch(() => undefined);
		};

		const invalidateRuns = (): void => {
			queryClient.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.listRuns) }).catch(() => undefined);
			queryClient.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.getRun) }).catch(() => undefined);
		};

		// Each run event invalidates the run caches AND (for terminal outcomes) raises a completion toast so the operator
		// learns when ANY scheduled task finishes. Per-event handler identities are retained so cleanup can `off` them.
		const runHandlers = new Map<string, (payload: unknown) => void>(
			RUN_EVENTS.map((eventName) => [
				eventName,
				(payload: unknown): void => {
					invalidateRuns();
					notifySchedulerRunEvent(eventName, payload, tRef.current);
				},
			]),
		);

		connection.on(JOB_DEFINITION_CHANGED, invalidateJobs);
		for (const [eventName, handler] of runHandlers) {
			connection.on(eventName, handler);
		}

		let disposed = false;
		const startPromise = connection.start().catch((error: unknown) => {
			// A start aborted by our own cleanup (StrictMode double-invoke / fast remount) is not a real failure.
			if (disposed) {
				return;
			}
			// A hub that cannot connect must not break the page — TanStack Query still serves cached state. The hub is
			// best-effort live invalidation, so a connection failure is surfaced only to the console, never the user.
			console.warn("scheduler hub failed to start", error);
		});

		return () => {
			disposed = true;
			connection.off(JOB_DEFINITION_CHANGED, invalidateJobs);
			for (const [eventName, handler] of runHandlers) {
				connection.off(eventName, handler);
			}
			// Stop only AFTER start settles so cleanup never aborts an in-flight negotiation (the "stopped during
			// negotiation" race that left the hub permanently disconnected under StrictMode / fast remounts).
			startPromise.finally(() => {
				connection.stop().catch((error: unknown) => {
					console.warn("scheduler hub failed to stop", error);
				});
			});
		};
	}, [queryClient]);
}
