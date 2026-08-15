import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useState } from "react";
import { z } from "zod";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { mergeTrainingLogs, type TrainingLogEntry, trainingLogEntries } from "@/features/training/models/TrainingModels";
import { trainingQueryKeys } from "@/features/training/queries/useTrainingQueries";

const statusChanged = "trainingRuntime.statusChanged";

const eventSchema = z.object({
	phase: z.string(),
	appendedLogStartSequence: z.number().int().nonnegative(),
	appendedLogLines: z.array(z.string()),
	terminal: z.boolean(),
	sanitizedError: z.string().nullable(),
});

const emptyState = {
	phase: null as string | null,
	logEntries: [] as readonly TrainingLogEntry[],
	error: null as string | null,
};

/**
 * Streams the runtime install's phase and log lines. There is exactly one machine-global runtime, so the hub
 * broadcasts to every Operator client and there is nothing to subscribe to — the connection is the subscription.
 */
export function useTrainingRuntimeHub(enabled = true) {
	const queryClient = useQueryClient();
	const [state, setState] = useState(emptyState);
	const reset = useCallback(() => setState(emptyState), []);

	useEffect(() => {
		if (!enabled) {
			return undefined;
		}
		const hub = acquireHubConnection("training/runtime/hub");
		const invalidateStatus = (): void => {
			queryClient
				.invalidateQueries({ queryKey: trainingQueryKeys.invalidationKey(trainingQueryKeys.ids.runtimeStatus) })
				.catch(() => undefined);
		};
		const handler = (payload: unknown): void => {
			const parsed = eventSchema.safeParse(payload);
			if (!parsed.success) {
				return;
			}
			const event = parsed.data;
			setState((current) => ({
				phase: event.phase,
				logEntries: mergeTrainingLogs(current.logEntries, trainingLogEntries(event.appendedLogStartSequence, event.appendedLogLines)),
				error: event.sanitizedError,
			}));
			if (event.terminal) {
				invalidateStatus();
				// A finished install changes what the prerequisite report says about the lockfile and disk.
				queryClient
					.invalidateQueries({ queryKey: trainingQueryKeys.invalidationKey(trainingQueryKeys.ids.runtimePrerequisites) })
					.catch(() => undefined);
			}
		};
		hub.connection.on(statusChanged, handler);
		const unregisterReconnected = hub.onReconnected(() => {
			// The local log is a partial view of a ring the server owns; after a gap the only honest move is to drop it
			// and let the status query supply the retained window.
			setState(emptyState);
			invalidateStatus();
		});
		return () => {
			unregisterReconnected();
			hub.connection.off(statusChanged, handler);
			hub.release();
		};
	}, [enabled, queryClient]);

	return { ...state, reset };
}
