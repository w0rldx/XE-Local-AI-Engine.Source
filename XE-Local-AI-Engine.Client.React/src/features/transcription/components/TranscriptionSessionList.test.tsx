// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { TranscriptionSessionList } from "@/features/transcription/components/TranscriptionSessionList";
import type { TranscriptionSessionView } from "@/features/transcription/models/TranscriptionModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

function session(overrides: Partial<TranscriptionSessionView> = {}): TranscriptionSessionView {
	return {
		id: "session-1",
		title: "Standup recording",
		status: "Completed",
		sourceKind: "File",
		modelId: "base",
		detectedLanguage: "en",
		durationMs: 60_000,
		segmentCount: 12,
		createdAtUtc: 1_700_000_000_000,
		updatedAtUtc: 1_700_000_060_000,
		...overrides,
	};
}

describe("TranscriptionSessionList", () => {
	afterEach(() => {
		cleanup();
	});

	it("renders a card per session with its title, segment count and status", () => {
		renderWithProviders(
			<TranscriptionSessionList sessions={[session()]} deletingSessionId={null} onOpen={vi.fn()} onDelete={vi.fn()} />,
		);

		const card = screen.getByTestId("transcription-session-card-session-1");
		expect(card.textContent).toContain("Standup recording");
		expect(card.textContent).toContain("12 segments");
		expect(screen.getByTestId("transcription-session-status-session-1").textContent).toBe("Completed");
	});

	it("names an untitled recording rather than rendering an empty row", () => {
		renderWithProviders(
			<TranscriptionSessionList
				sessions={[session({ title: null })]}
				deletingSessionId={null}
				onOpen={vi.fn()}
				onDelete={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("transcription-session-card-session-1").textContent).toContain("Untitled recording");
	});

	it("opens the session when the card is clicked", () => {
		const onOpen = vi.fn();
		renderWithProviders(
			<TranscriptionSessionList sessions={[session()]} deletingSessionId={null} onOpen={onOpen} onDelete={vi.fn()} />,
		);

		fireEvent.click(screen.getByTestId("transcription-session-card-session-1"));

		expect(onOpen).toHaveBeenCalledWith("session-1");
	});

	// The delete control sits INSIDE the card, which is itself the navigation target: without stopPropagation the
	// operator would be navigated into the session they just asked to remove.
	it("deletes without also opening the row", () => {
		const onOpen = vi.fn();
		const onDelete = vi.fn();
		renderWithProviders(
			<TranscriptionSessionList sessions={[session()]} deletingSessionId={null} onOpen={onOpen} onDelete={onDelete} />,
		);

		fireEvent.click(screen.getByTestId("transcription-session-delete-session-1"));

		expect(onDelete).toHaveBeenCalledWith(expect.objectContaining({ id: "session-1" }));
		expect(onOpen).not.toHaveBeenCalled();
	});

	// English and German both distinguish one from many, and "1 segments" is the kind of wrong that reads as a bug in
	// the transcript rather than in the label.
	it("counts a single segment in the singular", () => {
		renderWithProviders(
			<TranscriptionSessionList
				sessions={[session({ segmentCount: 1 })]}
				deletingSessionId={null}
				onOpen={vi.fn()}
				onDelete={vi.fn()}
			/>,
		);

		const card = screen.getByTestId("transcription-session-card-session-1");
		expect(card.textContent).toContain("1 segment ");
		expect(card.textContent).not.toContain("1 segments");
	});

	// The card itself stays a plain container — an ARIA button around the delete icon would make that nested control
	// presentational. The title is the real button, so the keyboard reaches it natively.
	it("makes the session title a real button named after the session", () => {
		renderWithProviders(
			<TranscriptionSessionList sessions={[session()]} deletingSessionId={null} onOpen={vi.fn()} onDelete={vi.fn()} />,
		);

		expect(screen.getByRole("button", { name: "Standup recording" }).tagName).toBe("BUTTON");
	});

	it("names an untitled recording on the button rather than exposing a nameless one", () => {
		renderWithProviders(
			<TranscriptionSessionList
				sessions={[session({ title: null })]}
				deletingSessionId={null}
				onOpen={vi.fn()}
				onDelete={vi.fn()}
			/>,
		);

		expect(screen.getByRole("button", { name: "Untitled recording" })).toBeTruthy();
	});

	// The title button sits inside the card, whose onClick opens the same session: without stopPropagation one
	// activation would open it twice.
	it("opens the session exactly once when the title button is activated", () => {
		const onOpen = vi.fn();
		renderWithProviders(
			<TranscriptionSessionList sessions={[session()]} deletingSessionId={null} onOpen={onOpen} onDelete={vi.fn()} />,
		);

		fireEvent.click(screen.getByRole("button", { name: "Standup recording" }));

		expect(onOpen).toHaveBeenCalledExactlyOnceWith("session-1");
	});
});
