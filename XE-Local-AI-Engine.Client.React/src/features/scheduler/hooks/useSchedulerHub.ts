import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";

import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
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
			queryClient
				.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.listJobs) })
				.catch(() => undefined);
		};

		const invalidateRuns = (): void => {
			queryClient
				.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.listRuns) })
				.catch(() => undefined);
			queryClient
				.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.getRun) })
				.catch(() => undefined);
		};

		connection.on(JOB_DEFINITION_CHANGED, invalidateJobs);
		for (const eventName of RUN_EVENTS) {
			connection.on(eventName, invalidateRuns);
		}

		let disposed = false;
		const startPromise = connection.start().catch((error: unknown) => {
			// A start aborted by our own cleanup (StrictMode double-invoke / fast remount) is not a real failure.
			if (disposed) {
				return;
			}
			// A hub that cannot connect must not break the page — TanStack Query still serves cached state. The hub is
			// best-effort live invalidation, so a connection failure is surfaced only to the console, never the user.
			// biome-ignore lint/suspicious/noConsole: intentional best-effort warning for a tolerated hub failure.
			console.warn("scheduler hub failed to start", error);
		});

		return () => {
			disposed = true;
			connection.off(JOB_DEFINITION_CHANGED, invalidateJobs);
			for (const eventName of RUN_EVENTS) {
				connection.off(eventName, invalidateRuns);
			}
			// Stop only AFTER start settles so cleanup never aborts an in-flight negotiation (the "stopped during
			// negotiation" race that left the hub permanently disconnected under StrictMode / fast remounts).
			startPromise.finally(() => {
				connection.stop().catch((error: unknown) => {
					// biome-ignore lint/suspicious/noConsole: intentional best-effort warning for a tolerated hub failure.
					console.warn("scheduler hub failed to stop", error);
				});
			});
		};
	}, [queryClient]);
}
