// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { NewTranscriptionSessionDialog } from "@/features/transcription/components/NewTranscriptionSessionDialog";
import { renderWithProviders } from "@/test/RenderWithProviders";

function renderDialog(onSubmit = vi.fn()) {
	renderWithProviders(
		<NewTranscriptionSessionDialog
			opened={true}
			isSubmitting={false}
			uploadProgressPercent={0}
			onClose={vi.fn()}
			onSubmit={onSubmit}
		/>,
	);
	return onSubmit;
}

describe("NewTranscriptionSessionDialog", () => {
	afterEach(() => {
		cleanup();
	});

	// All four sources are rendered so the capture slice is a data change (drop `disabled`, add the branch) rather
	// than a layout change — but only File may be reachable here.
	it("offers all four sources with the three live ones disabled", () => {
		renderDialog();

		expect(screen.getByRole("radio", { name: "File" })).toHaveProperty("disabled", false);
		for (const label of ["Microphone", "System audio", "Microphone + system"]) {
			expect(screen.getByRole("radio", { name: label })).toHaveProperty("disabled", true);
		}
		expect(screen.getByTestId("new-transcription-session-dialog").textContent).toContain(
			"Live microphone and system-audio capture arrive in a later release.",
		);
	});

	// R15: the switch translates INTO English specifically — whisper has no other translation target — so the label
	// has to say so rather than offering a generic "translate".
	it("names English as the translation target", () => {
		renderDialog();

		expect(screen.getByText("Translate to English")).toBeDefined();
	});

	it("keeps submit disabled and submits nothing while no file is staged", () => {
		const onSubmit = renderDialog();

		const submit = screen.getByTestId("new-transcription-session-submit");
		expect(submit).toHaveProperty("disabled", true);

		fireEvent.click(submit);
		expect(onSubmit).not.toHaveBeenCalled();
	});

	// The upload endpoint never names the row, so the create call has to carry a title, and this dialog offers no
	// title field — the file's own name is what the operator already chose. An auto-detected language sends no
	// override at all, which is what the endpoint reads as "detect it".
	it("submits the staged file with its own name as the title and auto language", () => {
		const onSubmit = renderDialog();

		const file = new File(["audio"], "standup.wav", { type: "audio/wav" });
		// Mantine's FileInput renders a button plus a hidden native input, and the dialog is portaled out of the
		// render container — so the native input is reached through the document, not the RTL container.
		const nativeFileInput = document.querySelector('input[type="file"]');
		expect(nativeFileInput).not.toBeNull();
		fireEvent.change(nativeFileInput as HTMLInputElement, { target: { files: [file] } });
		fireEvent.click(screen.getByTestId("new-transcription-session-submit"));

		expect(onSubmit).toHaveBeenCalledWith({
			title: "standup.wav",
			sourceKind: "File",
			languageMode: "auto",
			languageOverride: null,
			translate: false,
			maxWindowSeconds: 5,
			channelAttribution: false,
			file,
		});
	});

	// The capture-window control belongs to the live sources, so it must not be on screen while File is the only
	// reachable one — its value still rides the create request at the endpoint's own default.
	it("hides the capture-window control for a file source", () => {
		renderDialog();

		expect(screen.queryByTestId("new-transcription-session-max-window")).toBeNull();
	});

	// The upload is a whole recording over a local socket, but a long one still takes visible seconds; without this
	// the dialog would sit on a spinner with no indication that anything was moving.
	it("shows the upload progress while a submit is in flight", () => {
		renderWithProviders(
			<NewTranscriptionSessionDialog
				opened={true}
				isSubmitting={true}
				uploadProgressPercent={42}
				onClose={vi.fn()}
				onSubmit={vi.fn()}
			/>,
		);

		const progress = screen.getByTestId("new-transcription-session-progress");
		expect(progress.querySelector("[role='progressbar']")?.getAttribute("aria-valuenow")).toBe("42");
	});

	// The copy shipped claiming OGG was decoded natively, which sent an operator whose upload was refused looking for a
	// bug rather than for ffmpeg. It names which containers the daemon reads and which need the binary, so it has to
	// agree with AudioContainerSniffer's native and transcode lists.
	it("names OGG among the containers that need ffmpeg, not the ones read directly", () => {
		renderDialog();

		const description = screen.getByText(/are read directly/);
		expect(description.textContent).toContain("WAV, MP3 and FLAC are read directly");
		expect(description.textContent).toMatch(/OGG, M4A and WebM need ffmpeg/);
	});

	it("shows no progress bar before a submit starts", () => {
		renderDialog();

		expect(screen.queryByTestId("new-transcription-session-progress")).toBeNull();
	});
});
