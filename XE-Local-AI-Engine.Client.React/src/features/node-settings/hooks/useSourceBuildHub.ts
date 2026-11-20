import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useState } from "react";
import { z } from "zod";

import { operationInvalidationKey } from "@/core/api/QueryKeys";
import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import {
	mergeSourceBuildLogs,
	type SourceBuildLogEntry,
	sourceBuildIdentity,
	sourceBuildLogEntries,
} from "@/features/node-settings/models/SourceBuildModels";
import { imageRuntimeQueryIds } from "@/features/node-settings/queries/useImageRuntime";
import { localRuntimeQueryIds } from "@/features/node-settings/queries/useLocalRuntime";

// One source-build hub: where it lives, the event it pushes, and the queries a terminal event or a reconnect makes
// stale (the build status and the runtime it installs).
interface SourceBuildStream {
	readonly hubPath: string;
	readonly statusChanged: string;
	readonly staleQueryIds: readonly string[];
}

export const llamaCppSourceBuildStream: SourceBuildStream = {
	hubPath: "model-fit/llamacpp/source-build/hub",
	statusChanged: "llamaCppSourceBuild.statusChanged",
	staleQueryIds: [localRuntimeQueryIds.sourceBuildStatus, localRuntimeQueryIds.llamaCppRuntime],
};

export const imageRuntimeSourceBuildStream: SourceBuildStream = {
	hubPath: "images/runtime/source-build/hub",
	statusChanged: "stableDiffusionCppSourceBuild.statusChanged",
	staleQueryIds: [imageRuntimeQueryIds.sourceBuildStatus, imageRuntimeQueryIds.runtime],
};

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

export function useSourceBuildHub(stream: SourceBuildStream, enabled = true) {
	const queryClient = useQueryClient();
	const [state, setState] = useState(emptyState);
	const reset = useCallback(() => setState(emptyState), []);

	useEffect(() => {
		if (!enabled) {
			return undefined;
		}
		const hub = acquireHubConnection(stream.hubPath);
		const invalidate = (): void => {
			for (const queryId of stream.staleQueryIds) {
				queryClient.invalidateQueries({ queryKey: operationInvalidationKey(queryId) }).catch(() => undefined);
			}
		};
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
				invalidate();
			}
		};
		hub.connection.on(stream.statusChanged, handler);
		const unregisterReconnected = hub.onReconnected(() => {
			setState(emptyState);
			invalidate();
		});
		return () => {
			unregisterReconnected();
			hub.connection.off(stream.statusChanged, handler);
			hub.release();
		};
	}, [stream, enabled, queryClient]);

	return { ...state, reset };
}
