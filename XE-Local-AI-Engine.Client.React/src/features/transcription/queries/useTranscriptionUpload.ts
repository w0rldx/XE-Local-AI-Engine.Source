import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";

import { uploadTranscriptionAudio } from "@/core/api/generated";
import {
	toTranscriptionSessionDetailView,
	type TranscriptionSessionDetailView,
} from "@/features/transcription/models/TranscriptionModels";
import { invalidate, transcriptionQueryIds } from "@/features/transcription/queries/useTranscriptionQueries";

// The generated `uploadTranscriptionAudio` serializes the body to FormData; the instance's FormData interceptor drops
// the JSON Content-Type default so the browser sets the multipart boundary. `onUploadProgress` is an axios option and
// passes through the SDK call. The ProblemDetails interceptor turns any non-2xx, including the typed 415, into an
// ApiError.
//
// The endpoint answers 200 for EVERY finished outcome (succeeded, canceled, runtime-failed), so the caller reads the
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
			const { data } = await uploadTranscriptionAudio({
				path: { sessionId },
				body: { file },
				onUploadProgress: (event) => {
					if (event.total) {
						setProgressPercent((event.loaded / event.total) * 100);
					}
				},
			});
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
