// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import { unsupportedContainerDetail } from "@/features/transcription/models/TranscriptionModels";
import { useTranscriptionUpload } from "@/features/transcription/queries/useTranscriptionUpload";
import { domainErrorRoute, jsonRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const sessionId = "11111111-0000-4000-8000-000000000001";

function completedDetail() {
	return {
		session: {
			id: sessionId,
			title: "standup.wav",
			status: "Completed",
			sourceKind: "File",
			modelId: "base",
			segmentCount: 1,
			createdAtUtc: 1,
			updatedAtUtc: 2,
		},
		segments: [{ id: "segment-1", seq: 1, startMs: 0, endMs: 900, text: "hello", channel: "Mono" }],
		config: { languageMode: "auto", translate: false, maxWindowSeconds: 5, channelAttribution: false },
	};
}

function audioFile() {
	return new File(["audio"], "standup.wav", { type: "audio/wav" });
}

describe("useTranscriptionUpload", () => {
	it("returns the finished session and invalidates the session and list queries", async () => {
		server.use(jsonRoute("post", `transcription/sessions/${sessionId}/file`, completedDetail()));
		const { wrapper, queryClient } = createProvidersWrapper();
		const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");
		const { result } = renderHook(() => useTranscriptionUpload(), { wrapper });

		const detail = await result.current.uploadAudio({ sessionId, file: audioFile() });

		expect(detail.session.status).toBe("Completed");
		expect(detail.segments.map((segment) => segment.text)).toEqual(["hello"]);
		await waitFor(() => {
			// The generated hey-api query keys discriminate on `_id`; partial matching on it invalidates every cached
			// variant of the endpoint.
			const invalidatedOperations = invalidateSpy.mock.calls.map(
				([filters]) => (filters?.queryKey as [{ _id?: string }] | undefined)?.[0]?._id,
			);
			expect(invalidatedOperations).toContain("getTranscriptionSession");
			expect(invalidatedOperations).toContain("listTranscriptionSessions");
		});
	});

	// A transcription that FAILED is still a finished request: the endpoint answers 200 and parks the verdict on the
	// session row, so a caller that read the HTTP status would report success on a failed run.
	it("surfaces a runtime failure through the returned session rather than a rejection", async () => {
		const failed = completedDetail();
		server.use(
			jsonRoute("post", `transcription/sessions/${sessionId}/file`, {
				...failed,
				session: { ...failed.session, status: "Failed" },
				segments: [],
				errorCode: "runtime-failed",
				errorMessage: "The whisper runtime stopped.",
			}),
		);
		const { wrapper } = createProvidersWrapper();
		const { result } = renderHook(() => useTranscriptionUpload(), { wrapper });

		const detail = await result.current.uploadAudio({ sessionId, file: audioFile() });

		expect(detail.session.status).toBe("Failed");
		expect(detail.errorMessage).toBe("The whisper runtime stopped.");
	});

	// The 415 is a typed domain body, not ProblemDetails — the interceptor must still produce a non-empty ApiError
	// message, and the readable-container list has to survive onto the caller.
	it("rejects an unreadable container as an ApiError carrying the supported containers", async () => {
		server.use(
			domainErrorRoute("post", `transcription/sessions/${sessionId}/file`, 415, {
				reason: "unsupported-container",
				message: "This node cannot read a Matroska container.",
				detectedContainer: "Matroska",
				supportedContainers: ["Wav", "Mp3", "Flac", "Ogg"],
				ffmpegRequired: true,
			}),
		);
		const { wrapper } = createProvidersWrapper();
		const { result } = renderHook(() => useTranscriptionUpload(), { wrapper });

		const error = await result.current.uploadAudio({ sessionId, file: audioFile() }).catch((thrown: unknown) => thrown);

		expect(error).toBeInstanceOf(ApiError);
		expect((error as ApiError).message).toBe("This node cannot read a Matroska container.");
		expect(unsupportedContainerDetail(error)?.supportedContainers).toEqual(["Wav", "Mp3", "Flac", "Ogg"]);
	});
});
