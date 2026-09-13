// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { usePendingComposerTextStore } from "@/core/ui/stores/PendingComposerTextStore";
import { TranscriptionSessionPage } from "@/features/transcription/pages/TranscriptionSessionPage";
import { domainErrorRoute, jsonRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const navigate = vi.hoisted(() => vi.fn());

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

describe("TranscriptionSessionPage", () => {
	beforeEach(() => {
		navigate.mockClear();
		usePendingComposerTextStore.setState({ pendingText: "" });
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

	it("offers no send-to-chat control while there is nothing to send", async () => {
		server.use(jsonRoute("get", `transcription/sessions/${sessionId}`, detail({ status: "Transcribing" }, [])));
		renderWithProviders(<TranscriptionSessionPage sessionId={sessionId} />);

		await screen.findByTestId("transcription-session-status");
		expect(screen.queryByTestId("transcription-session-send-to-chat")).toBeNull();
	});
});
