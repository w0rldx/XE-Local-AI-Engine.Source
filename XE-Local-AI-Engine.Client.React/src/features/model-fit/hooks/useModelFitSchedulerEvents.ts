import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { modelRecommendationCheckTemplateId } from "@/features/model-fit/models/ModelFitModels";
import {
	isTerminalRefreshRunStatus,
	notifyModelFitRefreshEvent,
	notifyModelFitRefreshRun,
} from "@/features/model-fit/notifications/ModelFitRefreshNotifications";
import { modelFitInvalidationKey, modelFitQueryIds } from "@/features/model-fit/queries/useModelFit";
import { fetchScheduledJobRuns } from "@/features/scheduler/queries/useScheduler";

// Realtime authoritative refetch + operator feedback for the model-fit pages. Reuses the SAME scheduler SignalR hub
// and event-name conventions as useSchedulerHub (no second hub server) — but where useSchedulerHub invalidates only
// scheduler caches and ignores the payload, this hook reads the run event's templateId and reacts only to the reserved
// model-recommendation-check template. A model-fit refresh runs asynchronously through the scheduler, so when a run for
// that template reaches a terminal status the cached recommendation snapshot may have changed; we invalidate the
// latest-recommendations query and let TanStack Query refetch the canonical state, AND raise a transient toast so the
// operator gets immediate, actionable feedback (the POST that fired the run already returned 200).
//
// The data layer is the generated hey-api TanStack query layer, whose query keys are single-element arrays
// `[{ _id: "<operationId>", ... }]`. Invalidating with the `_id` partial object (via modelFitInvalidationKey)
// matches every cached (useCase, providerName) variant of the latest-recommendations query — the realtime
// analogue of the old latestRoot() prefix invalidation.
//
// Terminal events only: RunCompleted/RunFailed/RunCancelled change the stored snapshot (or its diagnostics).
// RunStarted/RunProgress carry no new cache state, so reacting to them would refetch needlessly — but they are still
// bound as documented no-ops (IGNORED_RUN_EVENTS below) to silence the SignalR "No client method" warning.
const TERMINAL_RUN_EVENTS = ["scheduler.runCompleted", "scheduler.runFailed", "scheduler.runCancelled"] as const;

// The SAME scheduler hub broadcasts these non-terminal events to EVERY connected client. This hook intentionally
// ignores RunStarted/RunProgress (they carry no cache state — see TERMINAL_RUN_EVENTS above), but leaving them unbound
// makes the @microsoft/signalr client log `No client method with the name 'scheduler.runstarted' found.` (it lowercases
// target names) every time a run starts or progresses while a model-fit page is mounted — noisy and alarming though not
// an app defect. Binding no-op handlers marks the methods as handled so the client stays silent. This is NOT blind log
// suppression: the events are genuinely irrelevant here, we just tell the client so explicitly. Registered and torn
// down alongside the terminal handlers (see the connection effect) so the subscription symmetry is preserved.
const IGNORED_RUN_EVENTS = ["scheduler.runStarted", "scheduler.runProgress"] as const;

// Tolerance when comparing the client-side "started watching" timestamp against the server-stamped run fire time,
// to absorb client/server clock skew in the on-connect catch-up.
const CLOCK_SKEW_TOLERANCE_MS = 1000;

// Sanitized run-lifecycle payload (camelCase wire shape of SchedulerRunHubEvent). The event NAME discriminates the
// outcome, so `status` is intentionally NOT typed — its C# enum wire-casing is unverified. errorMessage/runId are the
// only extra fields read, and they are narrowed defensively because the payload is untrusted wire data.
interface SchedulerRunEventPayload {
	templateId?: string;
	errorMessage?: string | null;
	runId?: string;
}

function isModelRecommendationRun(payload: unknown): boolean {
	return (
		typeof payload === "object" &&
		payload !== null &&
		(payload as SchedulerRunEventPayload).templateId === modelRecommendationCheckTemplateId
	);
}

// Defensive narrowing of the two extra wire fields. Anything not a string becomes undefined so the toast layer never
// renders non-text data (the body is shown verbatim, no HTML).
function readRefreshFields(payload: unknown): { errorMessage?: string; runId?: string } {
	if (typeof payload !== "object" || payload === null) {
		return {};
	}
	const candidate = payload as SchedulerRunEventPayload;
	return {
		errorMessage: typeof candidate.errorMessage === "string" ? candidate.errorMessage : undefined,
		runId: typeof candidate.runId === "string" ? candidate.runId : undefined,
	};
}

// Subscribes to the scheduler hub for the lifetime of the mounting component and, when a model-recommendation-check run
// terminates, invalidates the model-fit latest-recommendations cache AND raises a toast (sticky red on failure carrying
// the sanitized reason, brief green on success, brief warn on cancel). Connection failures are tolerated silently
// (logged to console.warn) so a flaky hub never breaks the page — the queries still serve their last good data.
//
// SignalR push is the PRIMARY, instant delivery path. Because a push fired before the hub finishes its initial
// negotiation (the disabled-image run fails in ~30ms) — or during a reconnect gap — reaches no client and is never
// replayed, this hook ALSO runs a one-shot REST catch-up: it fetches the watched job's recent runs once and surfaces
// the latest terminal run (deduped against the push by run id in ModelFitRefreshNotifications). There is NO interval
// polling. The catch-up fires on three triggers, all keyed off the SAME run-id dedupe set so they never double-toast:
//   1. connection established (start success) — covers a completion missed during initial negotiation;
//   2. reconnected — covers a completion missed during a reconnect gap;
//   3. the watched job id resolving AFTER mount — the job id arrives a tick late from a separate jobs query, so a
//      job that completes before the id lands (and after the connect-time catch-up already early-returned for a
//      still-undefined id) would otherwise produce NO toast. This trigger lives in a SEPARATE effect keyed on the
//      job id so it re-runs when undefined → real WITHOUT tearing down / rebuilding the SignalR connection.
// The connection/handler subscription effect has STABLE deps so live pushes are never dropped and handlers are never
// double-registered. Pass the model-recommendation-check job id to enable the catch-up; without it only the push runs.
export function useModelFitSchedulerEvents(scheduledJobId?: string): void {
	const queryClient = useQueryClient();
	const { t } = useTranslation();

	// Hold the latest `t` in a ref so toast text uses the current language WITHOUT making `t` an effect dependency.
	// react-i18next hands back a NEW `t` on the ready transition / language change (and React StrictMode double-invokes
	// the effect); if `t` were a dep, each change would tear down and rebuild the SignalR connection mid-negotiation,
	// aborting it ("stopped during negotiation") so the hub never stays connected and no run events ever arrive.
	const tRef = useRef(t);

	// `scheduledJobId` is likewise read via a ref so it never rebuilds the connection (it resolves from a separate
	// jobs query a tick after mount). The watermark fixes "now" at mount so the catch-up never surfaces a run that
	// predates this page visit.
	const jobIdRef = useRef(scheduledJobId);
	useLayoutEffect(() => {
		tRef.current = t;
		jobIdRef.current = scheduledJobId;
	}, [scheduledJobId, t]);
	const [watermarkUtc] = useState(Date.now);

	// Gate for the late-job-id catch-up effect: true once the hub has connected at least once. The connection effect
	// already fires a catch-up the moment it connects, so the job-id effect must only fire its OWN catch-up AFTER a
	// connection exists — otherwise a job id present at mount would race the connect-time catch-up and fetch twice.
	const connectionEstablishedRef = useRef(false);

	// One-shot reconciliation: fetch the watched job's recent runs once and surface the latest terminal run (deduped
	// against any push — and against another catch-up — by run id in ModelFitRefreshNotifications). Covers the
	// connect-race / reconnect-gap / late-job-id cases WITHOUT polling. Reads job id and translations through refs;
	// the mount-time watermark is stable state. A stable identity keeps it out of the
	// connection effect's dependency set, so it never rebuilds the SignalR connection mid-negotiation.
	const runCatchUp = useCallback(async (): Promise<void> => {
		const jobId = jobIdRef.current;
		if (jobId === undefined) {
			return;
		}
		const sinceUtc = Math.max(0, watermarkUtc - CLOCK_SKEW_TOLERANCE_MS);
		try {
			const runs = await fetchScheduledJobRuns(queryClient, { scheduledJobId: jobId, fromUtc: sinceUtc });
			// Most-recent terminal run since the watermark — order-independent (don't assume the API's sort order).
			// Single pass tracking the running max instead of sorting the whole array just to read its first element.
			let terminalRun: (typeof runs)[number] | undefined;
			for (const run of runs) {
				if (run.actualFireTimeUtc === null || run.actualFireTimeUtc < sinceUtc || !isTerminalRefreshRunStatus(run.status)) {
					continue;
				}
				if (terminalRun === undefined || (run.actualFireTimeUtc ?? 0) > (terminalRun.actualFireTimeUtc ?? 0)) {
					terminalRun = run;
				}
			}
			if (terminalRun !== undefined) {
				notifyModelFitRefreshRun(terminalRun, tRef.current);
			}
		} catch {
			// Best-effort reconciliation — the push path stays primary and the cache still serves last-good state.
		}
	}, [queryClient, watermarkUtc]);

	// Connection + handler subscription effect. STABLE deps (queryClient, runCatchUp) so it is built exactly once per
	// mount: live pushes are never dropped and handlers are never double-registered when the job id resolves later.
	useEffect(() => {
		// Shared refcounted connection to the SAME scheduler hub useSchedulerHub uses — when both a scheduler page and a
		// model-fit page are mounted they now share ONE connection, and each hook's own handlers (registered via
		// connection.on below, removed on cleanup) coexist on it. Reused across mounts so navigation does not pay a fresh
		// negotiate + WebSocket upgrade.
		const hub = acquireHubConnection("scheduler/hub");
		const { connection } = hub;

		const handleTerminalRun = (eventName: (typeof TERMINAL_RUN_EVENTS)[number], payload: unknown): void => {
			if (!isModelRecommendationRun(payload)) {
				return;
			}

			// Authoritative state path (unchanged): TanStack Query refetches the canonical snapshot.
			queryClient.invalidateQueries({ queryKey: modelFitInvalidationKey(modelFitQueryIds.latest) }).catch(() => undefined);

			// Best-effort UI feedback. Stable id per run so a reconnect/duplicate broadcast replaces rather than stacks.
			const { errorMessage, runId } = readRefreshFields(payload);
			notifyModelFitRefreshEvent(eventName, { errorMessage, runId }, tRef.current);
		};

		// Every event name we bind on this connection, paired with its registered handler, so cleanup can unregister the
		// full set uniformly (terminal handlers AND the ignored-event no-ops below — see the return statement).
		const handlers: (readonly [string, (payload: unknown) => void])[] = TERMINAL_RUN_EVENTS.map((eventName) => {
			const handler = (payload: unknown): void => handleTerminalRun(eventName, payload);
			connection.on(eventName, handler);
			return [eventName, handler] as const;
		});

		// Documented no-ops for the non-terminal events this hook deliberately ignores (see IGNORED_RUN_EVENTS): binding
		// them keeps the SignalR client from logging "No client method with the name '...' found." Pushed into the SAME
		// handlers array so the cleanup below unregisters them exactly like the terminal handlers (symmetry preserved).
		for (const eventName of IGNORED_RUN_EVENTS) {
			const handler = (): void => undefined;
			connection.on(eventName, handler);
			handlers.push([eventName, handler]);
		}

		hub.onReconnected(() => {
			connectionEstablishedRef.current = true;
			runCatchUp().catch(() => undefined);
		});

		let disposed = false;
		// Run the connect-time catch-up once the shared connection is up. whenStarted resolves when the initial start
		// settles, or on the next microtask for a late subscriber that acquires while the connection is already connected;
		// the catch-up itself is a no-op when the hub failed to connect (it just does a best-effort REST fetch).
		hub.whenStarted.then(() => {
			// Only when the hub actually reached Connected (whenStarted also resolves after a failed initial start): the
			// established gate must stay false on failure, matching the original start-success-only catch-up.
			if (disposed || connection.state !== HubConnectionState.Connected) {
				return;
			}
			// Mark established so the late-job-id effect's own catch-up may fire from here on (and is skipped before this
			// point so it never double-fetches alongside this connect-time catch-up).
			connectionEstablishedRef.current = true;
			runCatchUp().catch(() => undefined);
		});

		return () => {
			disposed = true;
			for (const [eventName, handler] of handlers) {
				connection.off(eventName, handler);
			}
			// Release the shared lease: drops this handle's reconnected callback and, once the last subscriber releases,
			// stops the connection after the start promise settles (so cleanup never aborts an in-flight negotiation).
			hub.release();
		};
		// `t` is intentionally NOT a dependency — it is read via tRef so a new `t` reference never rebuilds the
		// connection mid-negotiation. Matches the stable-deps lifetime of useSchedulerHub. queryClient + runCatchUp stable.
	}, [queryClient, runCatchUp]);

	// Late-job-id catch-up. The job id arrives a tick after mount from a separate jobs query; when it transitions
	// undefined → real this effect re-runs and reconciles the run that completed before the id landed (the connect-time
	// catch-up above early-returned while the id was still undefined). Gated on connectionEstablishedRef so it never
	// races the connect-time catch-up when the id is already present at mount (that case is covered by the connect path,
	// keeping the steady mount to a single fetch). Run-id dedupe makes any overlap a no-op rather than a double toast.
	useEffect(() => {
		if (scheduledJobId === undefined || !connectionEstablishedRef.current) {
			return;
		}
		runCatchUp().catch(() => undefined);
	}, [scheduledJobId, runCatchUp]);
}
