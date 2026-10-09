// @vitest-environment jsdom

import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { LiveTranscriptPanel } from "@/features/transcription/components/LiveTranscriptPanel";
import type { LiveTranscriptView } from "@/features/transcription/hooks/useTranscriptionHub";
import { useTranscriptionCaptureStore } from "@/features/transcription/stores/TranscriptionCaptureStore";
import { createProvidersWrapper, renderWithProviders } from "@/test/RenderWithProviders";

function view(overrides: Partial<LiveTranscriptView> = {}): LiveTranscriptView {
	return {
		committed: [
			{ seq: 1, startMs: 1_000, endMs: 1_900, text: "good morning", channel: "you", confidence: null },
			{ seq: 2, startMs: 2_000, endMs: 2_800, text: "morning", channel: "others", confidence: null },
		],
		partials: { you: "and then", others: "hold on" },
		status: "Transcribing",
		bufferedMs: null,
		sourceQuietMs: null,
		lastSeq: 2,
		replayTruncated: false,
		...overrides,
	};
}

describe("LiveTranscriptPanel", () => {
	beforeEach(() => {
		useTranscriptionCaptureStore.setState({ showPartials: true });
		// jsdom implements no layout, so the stick-to-bottom follow has nothing to call.
		Element.prototype.scrollIntoView = vi.fn();
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

	// A long session grew the page without bound: the committed rows scroll inside a bounded box instead.
	it("scrolls a long transcript inside a bounded box and follows new rows", () => {
		const rows = (count: number) =>
			Array.from({ length: count }, (_, index) => ({
				seq: index + 1,
				startMs: index * 1_000,
				endMs: index * 1_000 + 900,
				text: `row ${index + 1}`,
				channel: "mono" as const,
				confidence: null,
			}));
		const { rerender } = render(<LiveTranscriptPanel view={view({ committed: rows(50), lastSeq: 50 })} />, {
			wrapper: createProvidersWrapper().wrapper,
		});

		const scroll = screen.getByTestId("transcription-committed-scroll");
		expect(scroll.contains(screen.getByTestId("transcription-committed-list"))).toBe(true);
		expect(scroll.style.maxHeight).not.toBe("");

		const follow = vi.mocked(Element.prototype.scrollIntoView);
		follow.mockClear();
		rerender(<LiveTranscriptPanel view={view({ committed: rows(51), lastSeq: 51 })} />);
		expect(follow).toHaveBeenCalled();
	});

	// The follow latch listens on the scroll box from its first commit, so the box must exist before the first row.
	it("keeps the scroll box mounted while the transcript is still empty", () => {
		const { rerender } = render(<LiveTranscriptPanel view={view({ committed: [], partials: {}, lastSeq: 0 })} />, {
			wrapper: createProvidersWrapper().wrapper,
		});
		const scroll = screen.getByTestId("transcription-committed-scroll");
		expect(scroll.contains(screen.getByTestId("transcription-live-empty"))).toBe(true);

		rerender(
			<LiveTranscriptPanel
				view={view({
					committed: [{ seq: 1, startMs: 0, endMs: 900, text: "row 1", channel: "mono", confidence: null }],
					lastSeq: 1,
				})}
			/>,
		);
		expect(screen.getByTestId("transcription-committed-scroll")).toBe(scroll);
		expect(scroll.contains(screen.getByTestId("transcription-committed-list"))).toBe(true);
	});

	// The captured application went silent: say so, but keep the session running.
	it("shows how long the captured application has been quiet, and nothing once audio returns", () => {
		const { rerender } = render(<LiveTranscriptPanel view={view({ sourceQuietMs: 12_400 })} />, {
			wrapper: createProvidersWrapper().wrapper,
		});

		expect(screen.getByTestId("transcription-source-quiet").textContent).toBe(
			"No audio from the captured application for 12 s. The session keeps running and resumes when it plays again.",
		);

		rerender(<LiveTranscriptPanel view={view({ sourceQuietMs: null })} />);
		expect(screen.queryByTestId("transcription-source-quiet")).toBeNull();
	});

	it("renders as a live panel before the hub has written anything", () => {
		renderWithProviders(<LiveTranscriptPanel view={null} />);

		expect(screen.getByTestId("transcription-live-panel").textContent).toContain("Audio is never stored.");
	});
});
