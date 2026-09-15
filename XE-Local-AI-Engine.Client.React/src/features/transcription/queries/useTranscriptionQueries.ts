import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	cancelTranscriptionSessionMutation,
	createTranscriptionSessionMutation,
	deleteTranscriptionSessionMutation,
	ejectTranscriptionRuntimeMutation,
	getTranscriptionRuntimeStatusOptions,
	getTranscriptionSessionOptions,
	listCaptureProcessesOptions,
	listTranscriptionModelsOptions,
	listTranscriptionSessionsOptions,
	startLiveTranscriptionSessionMutation,
	startProcessCaptureMutation,
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

// One session with its committed segments. A LIVE session never polls: the transcription hub pushes every committed
// segment and the terminal status, and the session view invalidates this query when a terminal status arrives, so a
// poll would re-read and re-decrypt a whole transcript every two seconds beside a socket that already delivered each
// row once. A FILE session has no hub subscription and the batch writer publishes nothing, so a row another tab is
// transcribing is only observable by re-reading it; that one case keeps the two-second poll.
export function useTranscriptionSession(sessionId: string) {
	return useQuery({
		...withResponseValidation(getTranscriptionSessionOptions({ path: { sessionId } })),
		select: toTranscriptionSessionDetailView,
		enabled: sessionId.length > 0,
		staleTime: 0,
		// `query.state.data` is the RAW response (select runs per observer), so the condition reads the wire shape.
		refetchInterval: (query) => {
			const session = query.state.data?.session;
			return session?.status === "Transcribing" && session.sourceKind === "File" ? 2_000 : false;
		},
	});
}

// Opens the live session on the node: after this returns the hub accepts `PushAudioFrame` for this row. Idempotent, so
// a retried start is safe; it answers 400 for a File session, 404 for an unknown one and 409 once the session is
// terminal. The row moved Created → Transcribing, so the detail and the list are refetched once for the badge; the hub,
// not a poll, reports everything after that.
export function useStartLiveTranscriptionSession() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (sessionId: string) => {
			const options = withResponseValidation(startLiveTranscriptionSessionMutation());
			return await options.mutationFn?.({ path: { sessionId } }, undefined as never);
		},
		onSuccess: async () => {
			await invalidate(queryClient, transcriptionQueryIds.session);
			await invalidate(queryClient, transcriptionQueryIds.sessions);
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

// The applications the node can target for server-side capture. Windows-only and live: a process that stopped playing
// is gone from the next read, so nothing is cached (`staleTime: 0`) and the dialog offers an explicit refresh. Enabled
// by the caller, never unconditionally — enumerating audio sessions is work the node should not do for a dialog that
// is closed or on a source that captures in the browser.
export function useCaptureProcesses(enabled: boolean) {
	return useQuery({
		...withResponseValidation(listCaptureProcessesOptions()),
		select: (data) => data.processes,
		enabled,
		staleTime: 0,
	});
}

// Attaches server-side per-application capture to a session that is ALREADY live (R30a): `live/start` first, this
// second, or the node answers 409 because there is no lane to push into. There is no scope argument — WASAPI captures
// the target and its descendants, and the dialog's copy says so rather than offering a choice the platform lacks.
export function useStartProcessCapture() {
	return useMutation({
		mutationFn: async ({ sessionId, processId }: { sessionId: string; processId: number }) => {
			const options = withResponseValidation(startProcessCaptureMutation());
			return await options.mutationFn?.({ path: { sessionId }, body: { processId } }, undefined as never);
		},
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
