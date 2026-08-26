import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useState } from "react";
import { z } from "zod";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import {
	mergeSourceBuildLogs,
	sourceBuildIdentity,
	sourceBuildLogEntries,
	type SourceBuildLogEntry,
} from "@/features/node-settings/models/SourceBuildModels";
import { localRuntimeInvalidationKey, localRuntimeQueryIds } from "@/features/node-settings/queries/useLocalRuntime";

const statusChanged = "llamaCppSourceBuild.statusChanged";
const eventSchema = z.object({
	phase: z.string(),
	appendedLogStartSequence: z.number().int().nonnegative(),
	appendedLogLines: z.array(z.string()),
	terminal: z.boolean(),
	sanitizedError: z.string().nullable(),
	currentBuild: z
		.object({
			buildId: z.uuid(),
			backend: z.enum(["cpu", "vulkan", "cuda"]),
			source: z.enum(["official", "custom"]),
			repository: z.string(),
			revisionMode: z.enum(["enginePinned", "defaultBranch", "explicitCommit"]),
			requestedCommit: z.string().nullable(),
			resolvedCommit: z.string().nullable(),
		})
		.nullable(),
});

const emptyState = {
	phase: null as string | null,
	logEntries: [] as readonly SourceBuildLogEntry[],
	error: null as string | null,
	buildIdentity: null as string | null,
};

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
				const identity = sourceBuildIdentity(event.currentBuild);
				const priorEntries = identity !== current.buildIdentity ? [] : current.logEntries;
				const appendedEntries = sourceBuildLogEntries(event.appendedLogStartSequence, event.appendedLogLines);
				return {
					phase: event.phase,
					logEntries: mergeSourceBuildLogs(priorEntries, appendedEntries),
					error: event.sanitizedError,
					buildIdentity: identity,
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
		const unregisterReconnected = hub.onReconnected(() => {
			setState(emptyState);
			queryClient
				.invalidateQueries({ queryKey: localRuntimeInvalidationKey(localRuntimeQueryIds.sourceBuildStatus) })
				.catch(() => undefined);
			queryClient
				.invalidateQueries({ queryKey: localRuntimeInvalidationKey(localRuntimeQueryIds.llamaCppRuntime) })
				.catch(() => undefined);
		});
		return () => {
			unregisterReconnected();
			hub.connection.off(statusChanged, handler);
			hub.release();
		};
	}, [enabled, queryClient]);

	return { ...state, reset };
}
