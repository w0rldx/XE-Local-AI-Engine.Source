import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useState } from "react";
import { z } from "zod";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { localRuntimeInvalidationKey, localRuntimeQueryIds } from "@/features/node-settings/queries/useLocalRuntime";

const statusChanged = "llamaCppSourceBuild.statusChanged";
const maxLogLines = 2000;
const eventSchema = z.object({
	phase: z.string(),
	appendedLogLines: z.array(z.string()),
	terminal: z.boolean(),
	sanitizedError: z.string().nullable(),
});

const emptyState = { phase: null as string | null, logLines: [] as readonly string[], error: null as string | null };

export function useSourceBuildHub(enabled = true) {
	const queryClient = useQueryClient();
	const [state, setState] = useState(emptyState);
	const reset = useCallback(() => setState(emptyState), []);

	useEffect(() => {
		if (!enabled) {
			return undefined;
		}
		const hub = acquireHubConnection("model-fit/llamacpp/source-build/hub");
		const handler = (payload: unknown): void => {
			const parsed = eventSchema.safeParse(payload);
			if (!parsed.success) {
				return;
			}
			const event = parsed.data;
			setState((current) => {
				const appended = [...current.logLines, ...event.appendedLogLines];
				return {
					phase: event.phase,
					logLines: appended.slice(Math.max(0, appended.length - maxLogLines)),
					error: event.sanitizedError,
				};
			});
			if (event.terminal) {
				queryClient
					.invalidateQueries({ queryKey: localRuntimeInvalidationKey(localRuntimeQueryIds.sourceBuildStatus) })
					.catch(() => undefined);
				queryClient
					.invalidateQueries({ queryKey: localRuntimeInvalidationKey(localRuntimeQueryIds.llamaCppRuntime) })
					.catch(() => undefined);
			}
		};
		hub.connection.on(statusChanged, handler);
		return () => {
			hub.connection.off(statusChanged, handler);
			hub.release();
		};
	}, [enabled, queryClient]);

	return { ...state, reset };
}
