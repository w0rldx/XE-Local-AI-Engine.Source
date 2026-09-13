import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";

import type { XeLocalAiEngineClientEndpointsTranscriptionV1TranscriptionSessionDetailResponse as TranscriptionSessionDetailResponse } from "@/core/api/generated";
import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import {
	toTranscriptionSessionDetailView,
	type TranscriptionSessionDetailView,
} from "@/features/transcription/models/TranscriptionModels";
import { invalidate, transcriptionQueryIds } from "@/features/transcription/queries/useTranscriptionQueries";

// The audio upload goes through the shared axiosInstance rather than the generated SDK, exactly as
// useKnowledgeUpload does and for the same reason: the request is multipart/form-data, the axios instance defaults
// Content-Type to application/json, and hey-api's binary body handling does not reliably override that (the file
// would serialize to `{}`). Auth + XSRF still ride the instance interceptors, and the ProblemDetails interceptor
// turns any non-2xx — including the typed 415 — into an ApiError.
//
// The endpoint answers 200 for EVERY finished outcome (succeeded, cancelled, runtime-failed), so the caller reads the
// returned session's `status` / `errorCode` rather than the HTTP status. Only 400/404/415 reject.

export interface UseTranscriptionUploadResult {
	readonly isUploading: boolean;
	/** 0..100 while a file is in flight. */
	readonly progressPercent: number;
	uploadAudio(input: { sessionId: string; file: File }): Promise<TranscriptionSessionDetailView>;
}

export function useTranscriptionUpload(): UseTranscriptionUploadResult {
	const queryClient = useQueryClient();
	const [progressPercent, setProgressPercent] = useState(0);

	const mutation = useMutation({
		mutationFn: async ({ sessionId, file }: { sessionId: string; file: File }) => {
			setProgressPercent(0);
			const formData = new FormData();
			formData.append("file", file);
			const { data } = await axiosInstance.post<TranscriptionSessionDetailResponse>(
				buildLocalApiUrl(`transcription/sessions/${sessionId}/file`),
				formData,
				{
					headers: { "Content-Type": "multipart/form-data" },
					onUploadProgress: (event) => {
						if (event.total) {
							setProgressPercent((event.loaded / event.total) * 100);
						}
					},
				},
			);
			return toTranscriptionSessionDetailView(data);
		},
		onSuccess: async () => {
			await invalidate(queryClient, transcriptionQueryIds.session);
			await invalidate(queryClient, transcriptionQueryIds.sessions);
		},
	});

	return {
		isUploading: mutation.isPending,
		progressPercent,
		uploadAudio: mutation.mutateAsync,
	};
}
