import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";

import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
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
// RunStarted/RunProgress carry no new cache state, so reacting to them would refetch needlessly.
const TERMINAL_RUN_EVENTS = ["scheduler.runCompleted", "scheduler.runFailed", "scheduler.runCancelled"] as const;

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
// replayed, this hook ALSO runs a one-shot REST catch-up each time the connection establishes/reconnects: it fetches
// the watched job's recent runs once and surfaces the latest terminal run (deduped against the push by run id). There
// is NO interval polling — the catch-up fires only on connection-established events, so steady state is push-only.
// Pass the model-recommendation-check job id to enable the catch-up; without it only the push path runs.
export function useModelFitSchedulerEvents(scheduledJobId?: string): void {
	const queryClient = useQueryClient();
	const { t } = useTranslation();

	// Hold the latest `t` in a ref so toast text uses the current language WITHOUT making `t` an effect dependency.
	// react-i18next hands back a NEW `t` on the ready transition / language change (and React StrictMode double-invokes
	// the effect); if `t` were a dep, each change would tear down and rebuild the SignalR connection mid-negotiation,
	// aborting it ("stopped during negotiation") so the hub never stays connected and no run events ever arrive.
	const tRef = useRef(t);
	tRef.current = t;

	// `scheduledJobId` is likewise read via a ref so it never rebuilds the connection (it resolves from a separate
	// jobs query a tick after mount). The watermark fixes "now" at mount so the catch-up never surfaces a run that
	// predates this page visit.
	const jobIdRef = useRef(scheduledJobId);
	jobIdRef.current = scheduledJobId;
	// Lazy-initialized once at mount: useRef ignores all but its first argument, so passing Date.now() directly would
	// re-evaluate it on every render and throw the result away. Initialize to null and stamp "now" once.
	const watermarkUtcRef = useRef<number | null>(null);
	if (watermarkUtcRef.current === null) {
		watermarkUtcRef.current = Date.now();
	}

	useEffect(() => {
		const connection = new HubConnectionBuilder()
			.withUrl(buildLocalApiUrl("scheduler/hub"), {
				accessTokenFactory: () => useNodeAuthStore.getState().accessToken ?? "",
			})
			// Persistent notification channel (mounted for the page lifetime), so auto-reconnect after a transient drop —
			// otherwise live invalidation is silently lost for the rest of the session. Matches the scheduler hub precedent.
			.withAutomaticReconnect()
			.configureLogging(LogLevel.Warning)
			.build();

		const handleTerminalRun = (eventName: (typeof TERMINAL_RUN_EVENTS)[number], payload: unknown): void => {
			if (!isModelRecommendationRun(payload)) {
				return;
			}

			// Authoritative state path (unchanged): TanStack Query refetches the canonical snapshot.
			queryClient
				.invalidateQueries({ queryKey: modelFitInvalidationKey(modelFitQueryIds.latest) })
				.catch(() => undefined);

			// Best-effort UI feedback. Stable id per run so a reconnect/duplicate broadcast replaces rather than stacks.
			const { errorMessage, runId } = readRefreshFields(payload);
			notifyModelFitRefreshEvent(eventName, { errorMessage, runId }, tRef.current);
		};

		const handlers = TERMINAL_RUN_EVENTS.map((eventName) => {
			const handler = (payload: unknown): void => handleTerminalRun(eventName, payload);
			connection.on(eventName, handler);
			return [eventName, handler] as const;
		});

		// One-shot reconciliation: on every (re)connect, fetch the watched job's recent runs once and surface the latest
		// terminal run (deduped against any push by run id). Covers the connect-race / reconnect-gap WITHOUT polling.
		const runCatchUp = async (): Promise<void> => {
			const jobId = jobIdRef.current;
			if (jobId === undefined) {
				return;
			}
			// `current` was stamped at mount (see lazy init above); coalesce only to satisfy the number | null type.
			const sinceUtc = Math.max(0, (watermarkUtcRef.current ?? Date.now()) - CLOCK_SKEW_TOLERANCE_MS);
			try {
				const runs = await fetchScheduledJobRuns(queryClient, { scheduledJobId: jobId, fromUtc: sinceUtc });
				// Most-recent terminal run since the watermark — order-independent (don't assume the API's sort order).
				// Single pass tracking the running max instead of sorting the whole array just to read its first element.
				let terminalRun: (typeof runs)[number] | undefined;
				for (const run of runs) {
					if (
						run.actualFireTimeUtc === null ||
						run.actualFireTimeUtc < sinceUtc ||
						!isTerminalRefreshRunStatus(run.status)
					) {
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
		};

		connection.onreconnected(() => {
			runCatchUp().catch(() => undefined);
		});

		let disposed = false;
		const startPromise = connection.start().then(
			() => {
				if (!disposed) {
					runCatchUp().catch(() => undefined);
				}
			},
			(error: unknown) => {
				// A start aborted by our own cleanup (StrictMode double-invoke / fast remount) is not a real failure.
				if (disposed) {
					return;
				}
				// A hub that cannot connect must not break the page — TanStack Query still serves cached state. The hub is
				// best-effort live invalidation + feedback, so a connection failure is surfaced only to the console.
				console.warn("model-fit scheduler hub failed to start", error);
			},
		);

		return () => {
			disposed = true;
			for (const [eventName, handler] of handlers) {
				connection.off(eventName, handler);
			}
			// Stop only AFTER start settles so cleanup never aborts an in-flight negotiation (the "stopped during
			// negotiation" race that left the hub permanently disconnected under StrictMode / fast remounts).
			startPromise.finally(() => {
				connection.stop().catch((error: unknown) => {
					console.warn("model-fit scheduler hub failed to stop", error);
				});
			});
		};
		// `t` is intentionally NOT a dependency — it is read via tRef so a new `t` reference never rebuilds the
		// connection mid-negotiation. Matches the stable-deps lifetime of useSchedulerHub. queryClient is stable.
	}, [queryClient]);
}
