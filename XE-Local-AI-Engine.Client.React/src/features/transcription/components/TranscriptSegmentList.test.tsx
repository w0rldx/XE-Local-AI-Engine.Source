// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it } from "vitest";

import { TranscriptSegmentList } from "@/features/transcription/components/TranscriptSegmentList";
import type { TranscriptSegmentView } from "@/features/transcription/models/TranscriptionModels";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const sessionId = "11111111-0000-4000-8000-000000000001";

/** Answers the segment PUT, recording each body; `status` other than 200 answers the typed refusal instead. */
function segmentRoute(bodies: unknown[], status = 200) {
	server.use(
		http.put(localApiPath(`transcription/sessions/${sessionId}/segments/1`), async ({ request }) => {
			const body = (await request.json()) as { text: string };
			bodies.push(body);
			return status === 200
				? HttpResponse.json({
						id: "33333333-0000-4000-8000-000000000001",
						seq: 1,
						startMs: 0,
						endMs: 1_000,
						text: body.text,
						channel: "Mono",
					})
				: HttpResponse.json({ reason: "session-transcribing", message: "still transcribing" }, { status });
		}),
	);
}

function renderEditable(text = "the first line") {
	renderWithProviders(<TranscriptSegmentList segments={[segment({ text })]} sessionId={sessionId} editable={true} />);
}

function input(): HTMLTextAreaElement {
	return screen.getByTestId("transcript-segment-input-1") as HTMLTextAreaElement;
}

function segment(overrides: Partial<TranscriptSegmentView> = {}): TranscriptSegmentView {
	return {
		id: "segment-1",
		seq: 1,
		startMs: 0,
		endMs: 1_000,
		text: "the first line",
		channel: "Mono",
		confidence: null,
		...overrides,
	};
}

describe("TranscriptSegmentList", () => {
	afterEach(() => {
		cleanup();
	});

	it("renders one row per segment in the order it is given", () => {
		renderWithProviders(
			<TranscriptSegmentList
				segments={[
					segment({ id: "a", seq: 1, text: "first" }),
					segment({ id: "b", seq: 2, text: "second" }),
					segment({ id: "c", seq: 3, text: "third" }),
				]}
			/>,
		);

		const rows = screen.getAllByTestId(/^transcript-segment-\d+$/);
		expect(rows.map((row) => row.textContent)).toEqual([
			expect.stringContaining("first"),
			expect.stringContaining("second"),
			expect.stringContaining("third"),
		]);
	});

	// mm:ss.S, and the tenth is the point: the reader scrubs to the offset, so 61.25 s must read 01:01.2, not "1 min".
	it("formats each boundary as a mm:ss.S clip offset", () => {
		renderWithProviders(<TranscriptSegmentList segments={[segment({ startMs: 61_250, endMs: 605_900 })]} />);

		expect(screen.getByTestId("transcript-segment-1").textContent).toContain("01:01.2 – 10:05.9");
	});

	it("offers no edit control unless the list is editable", () => {
		renderWithProviders(<TranscriptSegmentList segments={[segment()]} sessionId={sessionId} />);

		expect(screen.queryByTestId("transcript-segment-edit-1")).toBeNull();
	});

	it("saves the trimmed text and closes the editor", async () => {
		const bodies: unknown[] = [];
		segmentRoute(bodies);
		renderEditable();

		fireEvent.click(screen.getByRole("button", { name: "Edit segment text" }));
		fireEvent.change(input(), { target: { value: "  the corrected line \n" } });
		fireEvent.click(screen.getByTestId("transcript-segment-save-1"));

		await waitFor(() => expect(screen.queryByTestId("transcript-segment-editor-1")).toBeNull());
		expect(bodies).toEqual([{ text: "the corrected line" }]);
	});

	it("saves on Ctrl+Enter and cancels on Escape", async () => {
		const bodies: unknown[] = [];
		segmentRoute(bodies);
		renderEditable();

		fireEvent.click(screen.getByTestId("transcript-segment-edit-1"));
		fireEvent.change(input(), { target: { value: "dropped draft" } });
		fireEvent.keyDown(input(), { key: "Escape" });
		expect(screen.getByTestId("transcript-segment-1").textContent).toContain("the first line");

		fireEvent.click(screen.getByTestId("transcript-segment-edit-1"));
		fireEvent.change(input(), { target: { value: "kept" } });
		fireEvent.keyDown(input(), { key: "Enter", ctrlKey: true });
		await waitFor(() => expect(bodies).toEqual([{ text: "kept" }]));
	});

	it("restores the original text on cancel without calling the node", () => {
		const bodies: unknown[] = [];
		segmentRoute(bodies);
		renderEditable();

		fireEvent.click(screen.getByTestId("transcript-segment-edit-1"));
		fireEvent.change(input(), { target: { value: "never saved" } });
		fireEvent.click(screen.getByTestId("transcript-segment-cancel-1"));

		expect(screen.getByTestId("transcript-segment-1").textContent).toContain("the first line");
		fireEvent.click(screen.getByTestId("transcript-segment-edit-1"));
		expect(input().value).toBe("the first line");
		expect(bodies).toEqual([]);
	});

	it.each([
		{ name: "empty", value: "   " },
		{ name: "unchanged", value: " the first line " },
		{ name: "over 8000 characters", value: "x".repeat(8001) },
	])("disables save for $name text", ({ value }) => {
		renderEditable();

		fireEvent.click(screen.getByTestId("transcript-segment-edit-1"));
		fireEvent.change(input(), { target: { value } });

		expect(screen.getByTestId("transcript-segment-save-1")).toHaveProperty("disabled", true);
	});

	// A session still transcribing is a wait, not a failure: the draft survives and the reason is named.
	it("keeps the editor open and names the reason when the session is still transcribing", async () => {
		const bodies: unknown[] = [];
		segmentRoute(bodies, 409);
		renderEditable();

		fireEvent.click(screen.getByTestId("transcript-segment-edit-1"));
		fireEvent.change(input(), { target: { value: "too early" } });
		fireEvent.click(screen.getByTestId("transcript-segment-save-1"));

		expect((await screen.findByTestId("transcript-segment-blocked-1")).textContent).toContain(
			"This session is still transcribing; edit it once it has finished.",
		);
		expect(input().value).toBe("too early");
	});

	it("shows a channel badge for You and Others but never for Mono", () => {
		renderWithProviders(
			<TranscriptSegmentList
				segments={[
					segment({ id: "a", seq: 1, channel: "Mono" }),
					segment({ id: "b", seq: 2, channel: "You" }),
					segment({ id: "c", seq: 3, channel: "Others" }),
				]}
			/>,
		);

		expect(screen.queryByTestId("transcript-segment-channel-1")).toBeNull();
		expect(screen.getByTestId("transcript-segment-channel-2").textContent).toBe("You");
		expect(screen.getByTestId("transcript-segment-channel-3").textContent).toBe("Others");
	});
});
