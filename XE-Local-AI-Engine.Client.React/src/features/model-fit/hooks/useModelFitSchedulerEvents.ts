import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";

import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { modelRecommendationCheckTemplateId } from "@/features/model-fit/models/ModelFitModels";
import { modelFitInvalidationKey, modelFitQueryIds } from "@/features/model-fit/queries/useModelFit";

// Realtime authoritative refetch for the model-fit pages. Reuses the SAME scheduler SignalR hub and event-name
// conventions as useSchedulerHub (no second hub server) — but where useSchedulerHub invalidates only scheduler
// caches and ignores the payload, this hook reads the run event's templateId and reacts only to the reserved
// model-recommendation-check template. A model-fit refresh runs asynchronously through the scheduler, so when a
// run for that template reaches a terminal status the cached recommendation snapshot may have changed; we
// invalidate the latest-recommendations query and let TanStack Query refetch the canonical state.
//
// The data layer is the generated hey-api TanStack query layer, whose query keys are single-element arrays
// `[{ _id: "<operationId>", ... }]`. Invalidating with the `_id` partial object (via modelFitInvalidationKey)
// matches every cached (useCase, providerName) variant of the latest-recommendations query — the realtime
// analogue of the old latestRoot() prefix invalidation.
//
// Terminal events only: RunCompleted/RunFailed/RunCancelled change the stored snapshot (or its diagnostics).
// RunStarted/RunProgress carry no new cache state, so reacting to them would refetch needlessly.
const TERMINAL_RUN_EVENTS = ["scheduler.runCompleted", "scheduler.runFailed", "scheduler.runCancelled"] as const;

// Sanitized run-lifecycle payload (camelCase wire shape of SchedulerRunHubEvent). Only templateId is needed
// here to decide whether the event concerns model-fit.
interface SchedulerRunEventPayload {
	templateId?: string;
}

function isModelRecommendationRun(payload: unknown): boolean {
	return (
		typeof payload === "object" &&
		payload !== null &&
		(payload as SchedulerRunEventPayload).templateId === modelRecommendationCheckTemplateId
	);
}

// Subscribes to the scheduler hub for the lifetime of the mounting component and invalidates the model-fit
// latest-recommendations cache when a model-recommendation-check run terminates. Connection failures are
// tolerated silently (logged to console.warn) so a flaky hub never breaks the page — the queries still serve
// their last good data and the user can refresh manually.
export function useModelFitSchedulerEvents(): void {
	const queryClient = useQueryClient();

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

		const invalidateLatest = (payload: unknown): void => {
			if (!isModelRecommendationRun(payload)) {
				return;
			}
			queryClient.invalidateQueries({ queryKey: modelFitInvalidationKey(modelFitQueryIds.latest) }).catch(() => undefined);
		};

		for (const eventName of TERMINAL_RUN_EVENTS) {
			connection.on(eventName, invalidateLatest);
		}

		connection.start().catch((error: unknown) => {
			// A hub that cannot connect must not break the page — TanStack Query still serves cached state. The hub is
			// best-effort live invalidation, so a connection failure is surfaced only to the console, never the user.
			// biome-ignore lint/suspicious/noConsole: intentional best-effort warning for a tolerated hub failure.
			console.warn("model-fit scheduler hub failed to start", error);
		});

		return () => {
			for (const eventName of TERMINAL_RUN_EVENTS) {
				connection.off(eventName, invalidateLatest);
			}
			connection.stop().catch((error: unknown) => {
				// biome-ignore lint/suspicious/noConsole: intentional best-effort warning for a tolerated hub failure.
				console.warn("model-fit scheduler hub failed to stop", error);
			});
		};
	}, [queryClient]);
}
