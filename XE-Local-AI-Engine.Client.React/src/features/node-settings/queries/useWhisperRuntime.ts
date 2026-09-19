import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef } from "react";

import {
	cancelWhisperCppSourceBuildMutation,
	ejectTranscriptionRuntimeMutation,
	getTranscriptionRuntimeStatusOptions,
	getWhisperCppSourceBuildPrerequisitesOptions,
	getWhisperCppSourceBuildStatusOptions,
	removeWhisperCppSourceBuildMutation,
	startWhisperCppSourceBuildMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { sourceBuildPrerequisiteStaleTime, sourceBuildRequest } from "@/features/node-settings/models/SourceBuildModels";
import {
	toWhisperRuntimeStatus,
	toWhisperSourceBuildPrerequisites,
	toWhisperSourceBuildStatus,
} from "@/features/node-settings/models/WhisperRuntimeSourceBuildMappers";
import type {
	WhisperSourceBackend,
	WhisperSourceBuildDraft,
} from "@/features/node-settings/models/WhisperRuntimeSourceBuildModels";
import { localRuntimeInvalidationKey } from "@/features/node-settings/queries/useLocalRuntime";

// Server state for the managed whisper.cpp source build. The runtime-status operation is the SAME one the
// transcription page reads; only the `select` differs, so an invalidation from either surface refreshes both.
// Not exported: the other two lanes export theirs for their hub hook to invalidate through, and this one has no hub.
const whisperRuntimeQueryIds = {
	runtime: "getTranscriptionRuntimeStatus",
	sourceBuildPrerequisites: "getWhisperCppSourceBuildPrerequisites",
	sourceBuildStatus: "getWhisperCppSourceBuildStatus",
} as const;

function invalidate(queryClient: ReturnType<typeof useQueryClient>, operationId: string): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: localRuntimeInvalidationKey(operationId) });
}

export function useWhisperRuntimeStatus(enabled = true) {
	return useQuery({
		...withResponseValidation(getTranscriptionRuntimeStatusOptions()),
		select: toWhisperRuntimeStatus,
		enabled,
	});
}

export function useWhisperSourceBuildPrerequisites(backend: WhisperSourceBackend, enabled = true) {
	return useQuery({
		...withResponseValidation(getWhisperCppSourceBuildPrerequisitesOptions({ query: { backend } })),
		select: toWhisperSourceBuildPrerequisites,
		enabled,
		staleTime: sourceBuildPrerequisiteStaleTime,
	});
}

/**
 * The build's only progress channel. Unlike llama.cpp and stable-diffusion.cpp, whisper.cpp registers no hub — the
 * provider's `IWhisperCppSourceBuildEventPublisher` has only the no-op floor implementation and the host substitutes
 * nothing — so this poll IS the live view, and its terminal transition is what the other two lanes get from their
 * hub's `terminal` event: the moment to re-read the runtime status that now carries (or no longer carries) the
 * adopted managed build.
 */
export function useWhisperSourceBuildStatus(enabled = true) {
	const queryClient = useQueryClient();
	const query = useQuery({
		...withResponseValidation(getWhisperCppSourceBuildStatusOptions()),
		select: toWhisperSourceBuildStatus,
		enabled,
		refetchInterval: (polled) => (polled.state.data?.isRunning === true ? 3_000 : false),
	});

	// One invalidation per finished build, keyed on the build and the phase it ended in, so a poll that keeps
	// returning the same terminal status does not re-invalidate on every tick.
	const settledBuild = useRef<string | null>(null);
	const terminalKey = query.data?.terminal === true ? `${query.data.currentBuild?.buildId ?? "none"}:${query.data.phase}` : null;
	useEffect(() => {
		if (terminalKey === null || settledBuild.current === terminalKey) {
			return;
		}
		settledBuild.current = terminalKey;
		invalidate(queryClient, whisperRuntimeQueryIds.runtime).catch(() => undefined);
	}, [terminalKey, queryClient]);

	return query;
}

export function useStartWhisperSourceBuild() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (draft: WhisperSourceBuildDraft) => {
			const options = withResponseValidation(startWhisperCppSourceBuildMutation());
			return await options.mutationFn?.({ body: sourceBuildRequest(draft) }, undefined as never);
		},
		onSuccess: () =>
			Promise.all([
				invalidate(queryClient, whisperRuntimeQueryIds.sourceBuildStatus),
				invalidate(queryClient, whisperRuntimeQueryIds.runtime),
			]),
	});
}

export function useCancelWhisperSourceBuild() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async () => {
			const options = withResponseValidation(cancelWhisperCppSourceBuildMutation());
			// `accepted` is the contract's transport placeholder for an otherwise body-less POST; the server neither
			// requires nor interprets it.
			return await options.mutationFn?.({ body: { accepted: true } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, whisperRuntimeQueryIds.sourceBuildStatus),
	});
}

export function useRemoveWhisperSourceBuild() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async () => {
			const options = withResponseValidation(removeWhisperCppSourceBuildMutation());
			return await options.mutationFn?.({ body: { accepted: true } }, undefined as never);
		},
		onSuccess: () =>
			Promise.all([
				invalidate(queryClient, whisperRuntimeQueryIds.sourceBuildStatus),
				invalidate(queryClient, whisperRuntimeQueryIds.runtime),
			]),
	});
}

/**
 * Unloads the resident whisper daemon. A build and a remove both need the mutation reservation, which
 * `WhisperRuntimeActivityGate` refuses while ANY process is resident, so this is the in-card recovery from the
 * `runtime-busy` refusal rather than a convenience.
 */
export function useEjectWhisperRuntime() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async () => {
			const options = withResponseValidation(ejectTranscriptionRuntimeMutation());
			return await options.mutationFn?.({ body: { accepted: true } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, whisperRuntimeQueryIds.runtime),
	});
}
