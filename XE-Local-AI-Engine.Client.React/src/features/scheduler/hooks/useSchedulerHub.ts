import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { schedulerInvalidationKey, schedulerQueryIds } from "@/features/scheduler/queries/useScheduler";

/* eslint-disable react-doctor/effect-needs-cleanup -- The cleanup does tear down: it offs every registered handler and releases the shared lease; the rule only recognizes teardown of a connection created inline in the effect. */

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

/** The run-history poll while the hub is down. */
export const SCHEDULER_POLL_INTERVAL_MS = 5_000;

/** Safety net while the hub is live, for a change no event announces: the retention sweep deleting old runs. */
export const SCHEDULER_PUSH_FLOOR_MS = 60_000;

export interface SchedulerHubState {
	/** Run-history cadence: the fallback while the hub is down, the floor while it is live, `undefined` while connecting. */
	readonly pollIntervalMs?: number;
}

// Subscribes to the scheduler SignalR hub for the lifetime of the mounting component. On a job-definition change
// the jobs list is invalidated; on any run event the run history + per-run detail are invalidated. The hub is a
// notification channel only — authoritative state is always refetched via TanStack Query. Connection failures never
// break the page: the hook hands out a fallback poll interval until the hub is connected again. A finished run's
// alert is not raised here: the app-wide operations feed (core/operations) does that on every page.
export function useSchedulerHub(): SchedulerHubState {
	const queryClient = useQueryClient();
	const [pollIntervalMs, setPollIntervalMs] = useState<number | undefined>(undefined);

	useEffect(() => {
		// Shared refcounted connection: reused across mounts so navigating back to a scheduler page does not pay a fresh
		// negotiate + WebSocket upgrade. Handlers below stay per-mount (registered on this connection, torn down on
		// cleanup) so this subscriber coexists with any other subscriber to the same hub.
		const hub = acquireHubConnection("scheduler/hub");
		const { connection } = hub;

		const invalidateJobs = (): void => {
			queryClient.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.listJobs) }).catch(() => undefined);
		};

		const invalidateRuns = (): void => {
			queryClient.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.listRuns) }).catch(() => undefined);
			queryClient.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.getRun) }).catch(() => undefined);
		};

		// Each run event invalidates the run caches. Per-event handler identities are retained so cleanup can `off` them.
		const runHandlers = new Map<string, () => void>(RUN_EVENTS.map((eventName) => [eventName, invalidateRuns]));

		connection.on(JOB_DEFINITION_CHANGED, invalidateJobs);
		for (const [eventName, handler] of runHandlers) {
			connection.on(eventName, handler);
		}

		let disposed = false;
		const degrade = (): void => {
			if (!disposed) {
				setPollIntervalMs(SCHEDULER_POLL_INTERVAL_MS);
			}
		};
		// The hub broadcasts with no replay, so whatever happened before this (re)connect is read once over REST.
		const resume = (): void => {
			if (disposed) {
				return;
			}
			if (connection.state !== HubConnectionState.Connected) {
				degrade();
				return;
			}
			invalidateJobs();
			invalidateRuns();
			setPollIntervalMs(SCHEDULER_PUSH_FLOOR_MS);
		};
		// release() drops these registrations, so the cleanup needs no unregister calls of its own.
		hub.onReconnecting(degrade);
		hub.onReconnected(resume);
		hub.onClosed(degrade);
		hub.whenStarted.then(resume).catch(degrade);

		return () => {
			disposed = true;
			connection.off(JOB_DEFINITION_CHANGED, invalidateJobs);
			for (const [eventName, handler] of runHandlers) {
				connection.off(eventName, handler);
			}
			// Release the shared lease: the manager stops the connection only after the LAST subscriber releases, and only
			// once the start promise settles (so cleanup never aborts an in-flight negotiation under StrictMode / fast remounts).
			hub.release();
		};
	}, [queryClient]);

	return { pollIntervalMs };
}
