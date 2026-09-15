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
// The interval this query used to run on, before the transcription hub existed (K-16). Kept as the window the
// negative assertions below wait out: if a poll ever comes back, this is the length it would have.
const formerPollMs = 2_000;

function transcribingDetail(sourceKind = "File") {
	return {
		session: {
			id: sessionId,
			title: "Standup recording",
			status: "Transcribing",
			sourceKind,
			modelId: "base",
			segmentCount: 0,
			createdAtUtc: 1_700_000_000_000,
			updatedAtUtc: 1_700_000_000_000,
		},
		segments: [],
		config: { languageMode: "auto", translate: false, maxWindowSeconds: 5, channelAttribution: false },
	};
}

describe("useTranscriptionSession", () => {
	// The hub delivers every committed segment and the terminal status of a LIVE session, and the session view
	// invalidates this query when one arrives. A poll beside that socket re-reads and re-decrypts the whole transcript
	// every two seconds for the life of every live session, so its absence is the invariant worth asserting.
	it("does not poll a live session that is still transcribing", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(`transcription/sessions/${sessionId}`), () => {
				reads += 1;
				return HttpResponse.json(transcribingDetail("Microphone"));
			}),
		);
		const { wrapper } = createProvidersWrapper();

		// The timers must be installed before the hook mounts, or an interval would be armed on the real clock and the
		// negative assertion below would pass for the wrong reason. `vi.waitFor` is the one that advances fake timers.
		vi.useFakeTimers();
		try {
			const { result } = renderHook(() => useTranscriptionSession(sessionId), { wrapper });

			await vi.waitFor(() => expect(result.current.isSuccess).toBe(true), { interval: 1 });
			expect(result.current.data?.session.status).toBe("Transcribing");

			await vi.advanceTimersByTimeAsync(formerPollMs * 5);
			expect(reads).toBe(1);
		} finally {
			vi.useRealTimers();
		}
	});

	// A FILE session is never subscribed on the hub and the batch writer publishes nothing, so a row another tab is
	// transcribing can only be observed by re-reading it. That one case keeps the poll.
	it("polls a file session that another client is still transcribing", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(`transcription/sessions/${sessionId}`), () => {
				reads += 1;
				return HttpResponse.json(transcribingDetail("File"));
			}),
		);
		const { wrapper } = createProvidersWrapper();

		vi.useFakeTimers();
		try {
			const { result } = renderHook(() => useTranscriptionSession(sessionId), { wrapper });

			await vi.waitFor(() => expect(result.current.isSuccess).toBe(true), { interval: 1 });
			expect(result.current.data?.session.sourceKind).toBe("File");

			await vi.advanceTimersByTimeAsync(formerPollMs * 2 + 50);
			expect(reads).toBeGreaterThanOrEqual(2);
		} finally {
			vi.useRealTimers();
		}
	});

	it("does not poll a terminal session either", async () => {
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
			await vi.advanceTimersByTimeAsync(formerPollMs * 5);
			expect(reads).toBe(1);
		} finally {
			vi.useRealTimers();
		}
	});
});
