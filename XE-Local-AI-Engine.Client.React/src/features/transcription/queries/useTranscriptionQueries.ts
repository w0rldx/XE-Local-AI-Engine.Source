import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	cancelTranscriptionSessionMutation,
	createTranscriptionSessionMutation,
	deleteTranscriptionSessionMutation,
	ejectTranscriptionRuntimeMutation,
	getTranscriptionRuntimeStatusOptions,
	getTranscriptionSessionOptions,
	listTranscriptionModelsOptions,
	listTranscriptionSessionsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import type { XeLocalAiEngineClientEndpointsTranscriptionV1CreateTranscriptionSessionRequest as CreateTranscriptionSessionRequest } from "@/core/api/generated";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import {
	toTranscriptionModelView,
	toTranscriptionRuntimeView,
	toTranscriptionSessionDetailView,
	toTranscriptionSessionView,
} from "@/features/transcription/models/TranscriptionModels";

// Server-state for the transcription feature. Reads use the generated hey-api `*Options()` (shared axios + TanStack
// AbortSignal wired automatically) with a `select` mapping the optional-field generated response into the strict
// domain view-model, each wrapped in withResponseValidation so a zod response-shape failure surfaces as an ApiError.
// The multipart audio upload is the one call that does NOT go through the generated SDK — see useTranscriptionUpload.

// Generated keys are object arrays; TanStack partial matching on `_id` invalidates every endpoint variant.
export const transcriptionQueryIds = {
	sessions: "listTranscriptionSessions",
	session: "getTranscriptionSession",
	runtime: "getTranscriptionRuntimeStatus",
	models: "listTranscriptionModels",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one transcription endpoint. */
function transcriptionInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

/** Invalidates every cached variant of one transcription endpoint. S3's hub pushes invalidate through this. */
export function invalidate(queryClient: ReturnType<typeof useQueryClient>, operationId: string): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: transcriptionInvalidationKey(operationId) });
}

// Session history, newest-first from the store. Paged on the wire (limit 1..200, offset >= 0); this surface shows one
// page and the defaults match the endpoint's own (50 / 0).
export function useTranscriptionSessions(limit = 50, offset = 0) {
	return useQuery({
		...withResponseValidation(listTranscriptionSessionsOptions({ query: { limit, offset } })),
		select: (data) => ({
			items: (data.items ?? []).map(toTranscriptionSessionView),
			totalCount: data.totalCount,
		}),
	});
}

// One session with its committed segments. A session that is still Transcribing polls, because there is no
// transcription hub until S3 — delete the refetchInterval when the hub lands (K-16).
export function useTranscriptionSession(sessionId: string) {
	return useQuery({
		...withResponseValidation(getTranscriptionSessionOptions({ path: { sessionId } })),
		select: toTranscriptionSessionDetailView,
		enabled: sessionId.length > 0,
		staleTime: 0,
		refetchInterval: (query) => {
			// A failed refetch leaves the LAST SUCCESSFUL data in place, so a session deleted from another tab keeps
			// reporting Transcribing and the poll would hammer a 404 for as long as the page stayed open. The query's
			// own status is the only thing that knows the last attempt failed.
			if (query.state.status === "error") {
				return false;
			}
			// `query.state.data` is the RAW wire shape, not the selected view — `select` runs per observer.
			return query.state.data?.session.status === "Transcribing" ? 2_000 : false;
		},
	});
}

// Creates the session row the upload then fills. The title must be supplied here: the upload endpoint never sets one.
export function useCreateTranscriptionSession() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (body: CreateTranscriptionSessionRequest) => {
			const options = withResponseValidation(createTranscriptionSessionMutation());
			return await options.mutationFn?.({ body }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, transcriptionQueryIds.sessions),
	});
}

// Deletes a session and its segments. Invalidates the list so the row disappears without a manual refresh.
export function useDeleteTranscriptionSession() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (sessionId: string) => {
			const options = withResponseValidation(deleteTranscriptionSessionMutation());
			return await options.mutationFn?.({ path: { sessionId } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, transcriptionQueryIds.sessions),
	});
}

// Cancels an in-flight transcription. Both the detail and the list carry the status, so both are invalidated.
export function useCancelTranscriptionSession() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (sessionId: string) => {
			const options = withResponseValidation(cancelTranscriptionSessionMutation());
			return await options.mutationFn?.({ path: { sessionId } }, undefined as never);
		},
		onSuccess: async () => {
			await invalidate(queryClient, transcriptionQueryIds.session);
			await invalidate(queryClient, transcriptionQueryIds.sessions);
		},
	});
}

// Whisper runtime status: state, backend, selected vs recommended model, VAD and the managed build. backend /
// binarySource / binaryVersion stay null until a daemon has spawned — a null is "not started", never "not installed".
export function useTranscriptionRuntimeStatus() {
	return useQuery({
		...withResponseValidation(getTranscriptionRuntimeStatusOptions()),
		select: toTranscriptionRuntimeView,
		staleTime: 10_000,
	});
}

// The transcription model catalogue with this box's installed flags and any download in flight. It polls itself while
// a weight transfer is running: the poll condition lives in the data, so making it a caller parameter would have every
// consumer mirror the same derivation.
export function useTranscriptionModels() {
	return useQuery({
		...withResponseValidation(listTranscriptionModelsOptions()),
		select: (data) => ({
			models: (data.models ?? []).map(toTranscriptionModelView),
			selectedModelId: data.selectedModelId ?? null,
			recommendedModelId: data.recommendedModelId,
		}),
		staleTime: 30_000,
		// `query.state.data` is the RAW response (select runs per observer), so the phase is read off the wire shape.
		refetchInterval: (query) =>
			query.state.data?.models.some((model) => model.download?.phase === "running") === true ? 5_000 : false,
	});
}

// Unloads the whisper model from memory (never a disk delete). Refreshes the runtime status so the card reflects it.
export function useEjectTranscriptionRuntime() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async () => {
			const options = withResponseValidation(ejectTranscriptionRuntimeMutation());
			// `accepted` is the contract's transport placeholder for an otherwise body-less POST; the server neither
			// requires nor interprets it.
			return await options.mutationFn?.({ body: { accepted: true } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, transcriptionQueryIds.runtime),
	});
}
