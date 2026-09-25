// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { LiveTranscriptPanel } from "@/features/transcription/components/LiveTranscriptPanel";
import type { LiveTranscriptView } from "@/features/transcription/hooks/useTranscriptionHub";
import { useTranscriptionCaptureStore } from "@/features/transcription/stores/TranscriptionCaptureStore";
import { renderWithProviders } from "@/test/RenderWithProviders";

function view(overrides: Partial<LiveTranscriptView> = {}): LiveTranscriptView {
	return {
		committed: [
			{ seq: 1, startMs: 1_000, endMs: 1_900, text: "good morning", channel: "you", confidence: null },
			{ seq: 2, startMs: 2_000, endMs: 2_800, text: "morning", channel: "others", confidence: null },
		],
		partials: { you: "and then", others: "hold on" },
		status: "Transcribing",
		bufferedMs: null,
		lastSeq: 2,
		replayTruncated: false,
		...overrides,
	};
}

describe("LiveTranscriptPanel", () => {
	beforeEach(() => {
		useTranscriptionCaptureStore.setState({ showPartials: true });
	});

	afterEach(() => {
		cleanup();
	});

	// Plan §4.2: RendersCommittedSegmentsAndOneProvisionalLinePerChannel
	// Provisional text is replaced wholesale on every update and can disagree with what is finally committed, so it
	// lives outside the committed list — a reader who cannot tell the two apart quotes text the transcript never kept.
	it("renders the committed segments and one provisional line per channel", () => {
		renderWithProviders(<LiveTranscriptPanel view={view()} />);

		const committed = screen.getByTestId("transcription-committed-list");
		expect(committed.textContent).toContain("good morning");
		expect(committed.textContent).toContain("morning");
		expect(screen.getByTestId("transcript-segment-1").textContent).toContain("00:01.0 – 00:01.9");
		expect(screen.getByTestId("transcription-partial-you").textContent).toBe("You: and then");
		expect(screen.getByTestId("transcription-partial-others").textContent).toBe("Others: hold on");
		// The provisional lines are siblings of the committed list, never rows inside it.
		expect(committed.textContent).not.toContain("and then");
	});

	it("renders an unattributed provisional line for a single-source session", () => {
		renderWithProviders(<LiveTranscriptPanel view={view({ partials: { mono: "still speaking" } })} />);

		expect(screen.getByTestId("transcription-partial-mono").textContent).toBe("still speaking");
		expect(screen.queryByTestId("transcription-partial-you")).toBeNull();
	});

	it("hides the provisional lines when the operator turned them off", () => {
		useTranscriptionCaptureStore.setState({ showPartials: false });
		renderWithProviders(<LiveTranscriptPanel view={view()} />);

		expect(screen.queryByTestId("transcription-partial-you")).toBeNull();
		expect(screen.getByTestId("transcription-committed-list").textContent).toContain("good morning");
	});

	it("records the provisional-text preference so the next session opens the same way", () => {
		renderWithProviders(<LiveTranscriptPanel view={view()} />);

		fireEvent.click(screen.getByLabelText("Show provisional text"));

		expect(useTranscriptionCaptureStore.getState().showPartials).toBe(false);
	});

	// Before the first segment is committed the panel is the only thing on screen, so it has to say that silence is
	// expected rather than render an empty box that reads as a failure.
	it("says it is listening before anything has been committed", () => {
		renderWithProviders(<LiveTranscriptPanel view={view({ committed: [], partials: {} })} />);

		expect(screen.getByTestId("transcription-live-empty").textContent).toBe(
			"Listening — the first segment appears once enough speech has been captured.",
		);
		expect(screen.queryByTestId("transcription-committed-list")).toBeNull();
	});

	it("renders as a live panel before the hub has written anything", () => {
		renderWithProviders(<LiveTranscriptPanel view={null} />);

		expect(screen.getByTestId("transcription-live-panel").textContent).toContain("Audio is never stored.");
	});
});
