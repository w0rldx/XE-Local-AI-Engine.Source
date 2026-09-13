// @vitest-environment jsdom

import { renderHook } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { describe, expect, it, vi } from "vitest";

import { useTranscriptionSession } from "@/features/transcription/queries/useTranscriptionQueries";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const sessionId = "11111111-0000-4000-8000-000000000001";
const sessionPollMs = 2_000;

function transcribingDetail() {
	return {
		session: {
			id: sessionId,
			title: "Standup recording",
			status: "Transcribing",
			sourceKind: "File",
			modelId: "base",
			segmentCount: 0,
			createdAtUtc: 1_700_000_000_000,
			updatedAtUtc: 1_700_000_000_000,
		},
		segments: [],
		config: { languageMode: "auto", translate: false, maxWindowSeconds: 5, channelAttribution: false },
	};
}

describe("useTranscriptionSession polling", () => {
	// A refetch that fails leaves the last successful data in place, so the session still reads as Transcribing. An
	// interval derived from that data alone would keep a deleted session (removed in another tab) on a 404 every two
	// seconds for as long as the page stayed open.
	it("stops polling once a refetch fails and does not resume", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(`transcription/sessions/${sessionId}`), () => {
				reads += 1;
				// The first read succeeds and arms the poll; the session is deleted elsewhere before the second.
				return reads === 1 ? HttpResponse.json(transcribingDetail()) : new HttpResponse(null, { status: 404 });
			}),
		);
		const { wrapper } = createProvidersWrapper();

		// The timers must be installed before the hook mounts, or TanStack arms its interval on the real clock and the
		// negative assertion below passes for the wrong reason. `vi.waitFor` (not RTL's) is the one that advances fake
		// timers.
		vi.useFakeTimers();
		try {
			const { result } = renderHook(() => useTranscriptionSession(sessionId), { wrapper });

			await vi.waitFor(() => expect(result.current.isSuccess).toBe(true), { interval: 1 });
			expect(reads).toBe(1);
			expect(result.current.data?.session.status).toBe("Transcribing");

			// One poll on: the session is gone and the query goes to error while keeping its last good data.
			await vi.advanceTimersByTimeAsync(sessionPollMs);
			await vi.waitFor(() => expect(result.current.isError).toBe(true), { interval: 1 });
			expect(reads).toBe(2);
			expect(result.current.data?.session.status).toBe("Transcribing");

			// Several intervals further on, nothing has been requested again.
			await vi.advanceTimersByTimeAsync(sessionPollMs * 5);
			expect(reads).toBe(2);
		} finally {
			vi.useRealTimers();
		}
	});

	it("keeps polling while the session is still transcribing", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(`transcription/sessions/${sessionId}`), () => {
				reads += 1;
				return HttpResponse.json(transcribingDetail());
			}),
		);
		const { wrapper } = createProvidersWrapper();

		vi.useFakeTimers();
		try {
			const { result } = renderHook(() => useTranscriptionSession(sessionId), { wrapper });

			await vi.waitFor(() => expect(result.current.isSuccess).toBe(true), { interval: 1 });
			await vi.advanceTimersByTimeAsync(sessionPollMs);
			await vi.waitFor(() => expect(reads).toBe(2), { interval: 1 });
		} finally {
			vi.useRealTimers();
		}
	});

	it("stops polling once the session reaches a terminal status", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(`transcription/sessions/${sessionId}`), () => {
				reads += 1;
				const detail = transcribingDetail();
				return HttpResponse.json({ ...detail, session: { ...detail.session, status: "Completed" } });
			}),
		);
		const { wrapper } = createProvidersWrapper();

		vi.useFakeTimers();
		try {
			const { result } = renderHook(() => useTranscriptionSession(sessionId), { wrapper });

			await vi.waitFor(() => expect(result.current.isSuccess).toBe(true), { interval: 1 });
			await vi.advanceTimersByTimeAsync(sessionPollMs * 5);
			expect(reads).toBe(1);
		} finally {
			vi.useRealTimers();
		}
	});
});
