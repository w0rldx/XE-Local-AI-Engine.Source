// @vitest-environment jsdom

import { act, cleanup, fireEvent, screen } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { usePendingComposerTextStore } from "@/core/ui/stores/PendingComposerTextStore";
import type { LiveTranscriptView } from "@/features/transcription/hooks/useTranscriptionHub";
import { TranscriptionSessionPage } from "@/features/transcription/pages/TranscriptionSessionPage";
import { useTranscriptionCaptureStore } from "@/features/transcription/stores/TranscriptionCaptureStore";
import { domainErrorRoute, jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const navigate = vi.hoisted(() => vi.fn());

// The hub has its own tests; the page's contract is which VIEW it mounts for which session, so the transport is a
// double. `useLiveTranscript` keeps reading the same push-fed query the real hook reads, so a test delivers a
// transcript by writing that cache entry — which is what the hub itself does, and what re-renders the page.
vi.mock("@/features/transcription/hooks/useTranscriptionHub", async () => {
	const { useQuery } = await import("@tanstack/react-query");
	return {
		useTranscriptionHub: () => ({
			connected: true,
			subscriptionReady: true,
			replayStalled: null,
			subscribeFailed: null,
			pushFrame: vi.fn(),
			endSession: vi.fn(),
		}),
		useLiveTranscript: (sessionId: string) =>
			useQuery({
				queryKey: ["liveTranscript", sessionId],
				queryFn: (): unknown => null,
				staleTime: Number.POSITIVE_INFINITY,
			}).data ?? null,
	};
});

// The orchestration has its own tests (`useLiveCapture.test.ts`); what the page owns is the REQUEST it hands the
// hook, which is where the session's own microphone is chosen.
const liveCapture = vi.hoisted(() => ({
	start: vi.fn(() => Promise.resolve()),
	stop: vi.fn(() => Promise.resolve()),
}));

vi.mock("@/features/transcription/capture/useLiveCapture", () => ({
	useLiveCapture: () => ({
		state: "idle",
		error: null,
		replayStalled: false,
		connected: true,
		subscribeFailed: null,
		start: liveCapture.start,
		stop: liveCapture.stop,
	}),
}));

// The app router is built from routeTree.gen.ts; a unit test only needs the navigate CALL, not a real route match.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigate,
}));

const sessionId = "11111111-0000-4000-8000-000000000001";

function segment(seq: number, overrides: Record<string, unknown> = {}) {
	return {
		id: `2222222${seq}-0000-4000-8000-00000000000${seq}`,
		seq,
		startMs: seq * 1000,
		endMs: seq * 1000 + 900,
		text: `line ${seq}`,
		channel: "Mono",
		...overrides,
	};
}

function detail(overrides: Record<string, unknown> = {}, segments = [segment(1), segment(2)]) {
	return {
		session: {
			id: sessionId,
			title: "Standup recording",
			status: "Completed",
			sourceKind: "File",
			modelId: "base",
			detectedLanguage: "en",
			segmentCount: segments.length,
			createdAtUtc: 1_700_000_000_000,
			updatedAtUtc: 1_700_000_060_000,
			...overrides,
		},
		segments,
		config: { languageMode: "auto", translate: false, maxWindowSeconds: 5, channelAttribution: false },
	};
}

function liveView(status: string, committed: LiveTranscriptView["committed"] = []): LiveTranscriptView {
	return { committed, partials: {}, status, lastSeq: committed.length, replayTruncated: false };
}

/** Delivers a live transcript the way the hub does: by writing the push-fed query the view reads. */
function pushLiveTranscript(
	queryClient: { setQueryData: (key: readonly unknown[], value: unknown) => unknown },
	view: LiveTranscriptView,
) {
	act(() => {
		queryClient.setQueryData(["liveTranscript", sessionId], view);
	});
}

describe("TranscriptionSessionPage", () => {
	beforeEach(() => {
		navigate.mockClear();
		liveCapture.start.mockClear();
		usePendingComposerTextStore.setState({ pendingText: "" });
		useTranscriptionCaptureStore.setState({ deviceIdBySession: {}, processIdBySession: {} });
	});

	afterEach(() => {
		cleanup();
	});

	// The store returns segments in Seq order, but the render order is the contract the transcript depends on, so the
	// page is proved against an out-of-order response rather than a helpfully sorted one.
	it("renders the committed segments in Seq order with their clip offsets", async () => {
		server.use(jsonRoute("get", `transcription/sessions/${sessionId}`, detail({}, [segment(2), segment(1)])));
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		await screen.findByTestId("transcript-segment-list");
		const rows = screen.getAllByTestId(/^transcript-segment-\d+$/);
		expect(rows.map((row) => row.textContent)).toEqual([expect.stringContaining("line 1"), expect.stringContaining("line 2")]);
		expect(rows[0]?.textContent).toContain("00:01.0 – 00:01.9");
	});

	it("shows no channel badge on a single-source recording", async () => {
		server.use(jsonRoute("get", `transcription/sessions/${sessionId}`, detail()));
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		await screen.findByTestId("transcript-segment-list");
		expect(screen.queryByTestId("transcript-segment-channel-1")).toBeNull();
		expect(screen.queryByTestId("transcript-segment-channel-2")).toBeNull();
	});

	it("names the session, its status and the detected language", async () => {
		server.use(jsonRoute("get", `transcription/sessions/${sessionId}`, detail()));
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		expect(await screen.findByRole("heading", { level: 2, name: "Standup recording" })).toBeDefined();
		expect(screen.getByTestId("transcription-session-status").textContent).toBe("Completed");
		expect(screen.getByTestId("transcription-session-language").textContent).toContain("en");
	});

	// A failed transcription is a 200 whose verdict lives on the row, so the reason has to be rendered from the
	// session rather than inferred from an HTTP status that never reported one.
	it("renders the stored failure reason for a failed run", async () => {
		server.use(
			jsonRoute("get", `transcription/sessions/${sessionId}`, {
				...detail({ status: "Failed" }, []),
				errorCode: "runtime-failed",
				errorMessage: "The whisper runtime stopped.",
			}),
		);
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		const failure = await screen.findByTestId("transcription-session-failure");
		expect(failure.textContent).toContain("The whisper runtime stopped.");
		// The code is the half a support conversation can search for, so it is rendered beside the prose.
		expect(failure.textContent).toContain("runtime-failed");
		expect(screen.getByTestId("transcription-session-empty")).toBeDefined();
	});

	it("offers cancel only while the session is still transcribing", async () => {
		server.use(jsonRoute("get", `transcription/sessions/${sessionId}`, detail()));
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		await screen.findByTestId("transcription-session-status");
		expect(screen.queryByTestId("transcription-session-cancel")).toBeNull();
	});

	it("reports a load failure inline", async () => {
		server.use(domainErrorRoute("get", `transcription/sessions/${sessionId}`, 404, { detail: "no such session" }));
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		const alert = await screen.findByTestId("transcription-session-error");
		expect(alert.textContent).toContain("no such session");
	});

	// The transcript travels through the pending-composer store rather than the URL: it is routinely tens of kilobytes
	// and a search param would put it in the browser history in plaintext.
	it("stages the joined transcript for the chat composer and navigates to chat", async () => {
		server.use(jsonRoute("get", `transcription/sessions/${sessionId}`, detail()));
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("transcription-session-send-to-chat"));

		expect(usePendingComposerTextStore.getState().pendingText).toBe("line 1 line 2");
		expect(navigate).toHaveBeenCalledWith({ to: "/chat" });
	});

	// A live session that has not finished is fed by the hub, not by the detail endpoint: the REST rows are only
	// written as the node commits them, so rendering them here would show a transcript frozen at page load.
	it("renders the live panel and the capture controls for a live session that has not finished", async () => {
		server.use(
			jsonRoute("get", `transcription/sessions/${sessionId}`, detail({ status: "Created", sourceKind: "Microphone" }, [])),
		);
		const { queryClient } = renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		expect(await screen.findByTestId("transcription-live-panel")).toBeDefined();
		pushLiveTranscript(
			queryClient,
			liveView("Transcribing", [{ seq: 1, startMs: 0, endMs: 900, text: "live line", channel: "mono", confidence: null }]),
		);
		expect((await screen.findByTestId("transcription-committed-list")).textContent).toContain("live line");
		expect(screen.getByTestId("transcription-capture-start")).toBeDefined();
		expect(screen.queryByTestId("transcript-segment-list")).toBeNull();
	});

	// M3: two sessions created on two different microphones must each capture from their own. The device is never
	// sent to the node, so the store is the only record — and reading a global slot here meant the session created
	// second silently decided what the session created first captured from.
	it("captures from the microphone this session was created on", async () => {
		useTranscriptionCaptureStore.setState({
			// The other session is listed FIRST, so anything that reads "the remembered device" rather than this
			// session's own entry picks up the wrong microphone.
			deviceIdBySession: { "99999999-0000-4000-8000-000000000009": "webcam-2", [sessionId]: "usb-microphone-7" },
		});
		server.use(
			jsonRoute("get", `transcription/sessions/${sessionId}`, detail({ status: "Created", sourceKind: "Microphone" }, [])),
		);
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("transcription-capture-start"));

		expect(liveCapture.start).toHaveBeenCalledWith({ kind: "microphone", deviceId: "usb-microphone-7" });
	});

	// A session with no remembered device captures from whatever the browser calls the default input, rather than
	// from a device id left behind by some other session.
	it("captures from the default input when this session has no remembered microphone", async () => {
		server.use(
			jsonRoute("get", `transcription/sessions/${sessionId}`, detail({ status: "Created", sourceKind: "Microphone" }, [])),
		);
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("transcription-capture-start"));

		expect(liveCapture.start).toHaveBeenCalledWith({ kind: "microphone", deviceId: undefined });
	});

	// S5 / the bug this file would have caught: `isLive` used to be derived from the capture REQUEST, and an
	// application-capture session has none — the node records the application. The session rendered as if it had
	// finished, with the persisted rows and no controls, while it was still transcribing.
	it("renders the live panel and the capture controls for an application-capture session", async () => {
		useTranscriptionCaptureStore.setState({ processIdBySession: { [sessionId]: 4242 } });
		server.use(
			jsonRoute(
				"get",
				`transcription/sessions/${sessionId}`,
				detail({ status: "Transcribing", sourceKind: "ApplicationProcess" }, []),
			),
		);
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		expect(await screen.findByTestId("transcription-live-panel")).toBeDefined();
		expect(screen.getByTestId("transcription-capture-start")).toBeDefined();
		expect(screen.queryByTestId("transcript-segment-list")).toBeNull();
	});

	// The pid never reaches the node on the create request, so this store is the only record of which application the
	// session belongs to — and the node needs it back to attach its recorder.
	it("starts capture against the application this session was created for", async () => {
		useTranscriptionCaptureStore.setState({
			processIdBySession: { "99999999-0000-4000-8000-000000000009": 111, [sessionId]: 4242 },
		});
		server.use(
			jsonRoute(
				"get",
				`transcription/sessions/${sessionId}`,
				detail({ status: "Created", sourceKind: "ApplicationProcess" }, []),
			),
		);
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("transcription-capture-start"));

		expect(liveCapture.start).toHaveBeenCalledWith({ kind: "process", processId: 4242 });
	});

	// Clearing site data loses the pid, and there is no way to recover it: the node was never told which application
	// this row belongs to. Saying so beats a Start button that would post a process id nobody chose.
	it("refuses to start an application capture whose process this browser no longer remembers", async () => {
		server.use(
			jsonRoute(
				"get",
				`transcription/sessions/${sessionId}`,
				detail({ status: "Created", sourceKind: "ApplicationProcess" }, []),
			),
		);
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		expect(await screen.findByTestId("transcription-session-process-missing")).toBeDefined();
		expect(screen.queryByTestId("transcription-capture-start")).toBeNull();
		expect(liveCapture.start).not.toHaveBeenCalled();
	});

	it("keeps the persisted transcript for a live session that already finished", async () => {
		server.use(
			jsonRoute("get", `transcription/sessions/${sessionId}`, detail({ status: "Completed", sourceKind: "MicrophoneAndSystem" })),
		);
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		expect(await screen.findByTestId("transcript-segment-list")).toBeDefined();
		expect(screen.queryByTestId("transcription-live-panel")).toBeNull();
		expect(screen.queryByTestId("transcription-capture-start")).toBeNull();
	});

	it("never offers capture for a file session", async () => {
		server.use(jsonRoute("get", `transcription/sessions/${sessionId}`, detail({ status: "Transcribing" })));
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		await screen.findByTestId("transcript-segment-list");
		expect(screen.queryByTestId("transcription-capture-start")).toBeNull();
	});

	// The node persists the last rows as it ends the session, so the detail has to be re-read before the REST view
	// takes over — otherwise the finished session renders whatever the transcript was when the page first loaded.
	it("re-reads the session once the hub reports a terminal status", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(`transcription/sessions/${sessionId}`), () => {
				reads += 1;
				return HttpResponse.json(
					reads === 1
						? detail({ status: "Transcribing", sourceKind: "Microphone" }, [])
						: detail({ status: "Completed", sourceKind: "Microphone" }),
				);
			}),
		);
		const { queryClient } = renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);
		await screen.findByTestId("transcription-live-panel");
		pushLiveTranscript(queryClient, liveView("Transcribing"));

		pushLiveTranscript(queryClient, liveView("Completed"));

		expect(await screen.findByTestId("transcript-segment-list")).toBeDefined();
		expect(reads).toBeGreaterThan(1);
		expect(screen.queryByTestId("transcription-live-panel")).toBeNull();
	});

	it("offers no send-to-chat control while there is nothing to send", async () => {
		server.use(jsonRoute("get", `transcription/sessions/${sessionId}`, detail({ status: "Transcribing" }, [])));
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		await screen.findByTestId("transcription-session-status");
		expect(screen.queryByTestId("transcription-session-send-to-chat")).toBeNull();
	});
});
