// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { NewTranscriptionSessionDialog } from "@/features/transcription/components/NewTranscriptionSessionDialog";
import { useTranscriptionCaptureStore } from "@/features/transcription/stores/TranscriptionCaptureStore";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

// The dialog reads the whisper runtime status to decide whether the Windows application source exists at all, so
// every test in this file answers that endpoint. The body is the runtime response in full because the generated
// client validates it: a partial one parks the query in an error state and the flag would read false for the wrong
// reason.
function runtimeBody(processCaptureSupported: boolean) {
	return {
		enabled: true,
		state: "ready",
		backend: "cuda",
		binarySource: "managed",
		binaryVersion: "1.8.0",
		loadedModelId: "base",
		selectedModelId: "base",
		recommendedModelId: "base",
		supportsTranscode: true,
		idleTimeoutMinutes: 10,
		vadInstalled: true,
		processCaptureSupported,
		managedRuntime: null,
		activity: {
			activeTranscriptionCount: 0,
			spawnReadinessCount: 0,
			residentProcessCount: 1,
			mutationReserved: false,
			evictionReserved: false,
			isBusy: false,
		},
	};
}

function runtimeRoute(processCaptureSupported: boolean) {
	return jsonRoute("get", "transcription/runtime", runtimeBody(processCaptureSupported));
}

function processesRoute(processes: readonly { pid: number; name: string; hasAudio: boolean }[]) {
	return jsonRoute("get", "transcription/capture/processes", { supported: true, processes });
}

// The dialog also reads the weight catalogue, to say up front when this node has no model at all rather than letting
// the operator pick a file and discover it from the runtime's own exception after the upload.
function modelsRoute(installed: boolean) {
	return jsonRoute("get", "transcription/models", {
		models: [
			{
				id: "base",
				tier: "Base",
				sizeBytes: 147_951_465,
				approximateVramBytes: 838_860_800,
				approximateRamBytes: 576_716_800,
				englishOnly: false,
				installed,
				download: null,
			},
		],
		selectedModelId: null,
		recommendedModelId: "base",
	});
}

const applicationSource = "Application audio (Windows)";

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
	// The dialog opens on whatever was picked last time, so the remembered choice is reset per test rather than
	// carried between them by the persisted store.
	beforeEach(() => {
		useTranscriptionCaptureStore.setState({ lastSourceKind: "File", deviceIdBySession: {}, processIdBySession: {} });
		// Unsupported by default, so the tests that predate the Windows source keep the four-option dialog they assert
		// against; the two that need the fifth option register their own route over this one.
		server.use(runtimeRoute(false), processesRoute([]), modelsRoute(true));
	});

	afterEach(() => {
		cleanup();
	});

	it("offers all four sources, every one of them reachable", () => {
		renderDialog();

		for (const label of ["File", "Microphone", "System audio", "Microphone + system"]) {
			expect(screen.getByRole("radio", { name: label })).toHaveProperty("disabled", false);
		}
	});

	// The device list and the screen-share warning belong to different sources, so each is shown only where it means
	// something: a file upload has neither, and a screen share has no microphone to pick.
	// Chrome reports the OS default input under the literal id "default"; a sentinel with the same spelling made Mantine's
	// Select throw on a duplicate option and unmounted the whole dialog (found by the fake-device E2E).
	it('lists a browser device whose id is literally "default" beside the system-default option', async () => {
		const enumerateDevices = vi.fn().mockResolvedValue([
			{ kind: "audioinput", deviceId: "default", label: "Default - Built-in Microphone" },
			{ kind: "audioinput", deviceId: "default", label: "Default - Built-in Microphone" },
			{ kind: "videoinput", deviceId: "cam", label: "Camera" },
		]);
		vi.stubGlobal("navigator", { ...navigator, mediaDevices: { enumerateDevices } });
		try {
			renderDialog();
			fireEvent.click(screen.getByRole("radio", { name: "Microphone" }));

			await waitFor(() => expect(enumerateDevices).toHaveBeenCalledTimes(1));
			// Still mounted: the old sentinel made the Select throw on a duplicate value and unmounted the dialog.
			const picker = screen.getByTestId("new-transcription-session-device");
			expect(picker).toBeDefined();
			fireEvent.click(picker);
			expect(await screen.findByRole("option", { name: "Default - Built-in Microphone", hidden: true })).toBeDefined();
			// Deduped: the browser listed the same id twice, the picker shows it once.
			expect(screen.getAllByRole("option", { name: "Default - Built-in Microphone", hidden: true })).toHaveLength(1);
			expect(screen.getByRole("option", { name: "System default", hidden: true })).toBeDefined();
		} finally {
			vi.unstubAllGlobals();
		}
	});

	it("offers a microphone picker only for the microphone sources", () => {
		renderDialog();

		expect(screen.queryByTestId("new-transcription-session-device")).toBeNull();

		fireEvent.click(screen.getByRole("radio", { name: "Microphone" }));
		expect(screen.getByTestId("new-transcription-session-device")).toBeDefined();
		expect(screen.queryByTestId("new-transcription-session-share-hint")).toBeNull();
	});

	// R19: the browser re-prompts for a surface on every session and the app cannot engineer that away, so the dialog
	// says so up front — including that the picture is never recorded, which the picker itself does not say.
	it("warns that the browser asks for a surface on every system-audio session", () => {
		renderDialog();

		fireEvent.click(screen.getByRole("radio", { name: "System audio" }));

		const hint = screen.getByTestId("new-transcription-session-share-hint");
		expect(hint.textContent).toContain("asks which screen or window to share every time a session starts");
		expect(hint.textContent).toContain("never records the picture");
	});

	it("offers both the microphone picker and the share warning for a both-sources session", () => {
		renderDialog();

		fireEvent.click(screen.getByRole("radio", { name: "Microphone + system" }));

		expect(screen.getByTestId("new-transcription-session-device")).toBeDefined();
		expect(screen.getByTestId("new-transcription-session-share-hint")).toBeDefined();
	});

	// A live session has no file to stage and no name to borrow from one, so it submits with a time-stamped title and
	// no file at all — the audio arrives over the hub once the session view starts capture.
	it("submits a live session with no file and a time-stamped title", () => {
		const onSubmit = renderDialog();

		fireEvent.click(screen.getByRole("radio", { name: "Microphone" }));
		fireEvent.click(screen.getByTestId("new-transcription-session-submit"));

		expect(onSubmit).toHaveBeenCalledTimes(1);
		const values = onSubmit.mock.calls[0]?.[0] as {
			sourceKind: string;
			file: File | null;
			title: string;
			deviceId: string | null;
		};
		expect(values.sourceKind).toBe("Microphone");
		expect(values.file).toBeNull();
		expect(values.deviceId).toBeNull();
		expect(values.title).toContain("Live session");
	});

	// The file input is hidden, not cleared, when the source changes; the page routes on `file`, so a recording staged
	// before the switch would otherwise be uploaded instead of opening a live session.
	it("drops a file staged before the source was switched to a live one", () => {
		const onSubmit = renderDialog();
		const recording = new File([new Uint8Array(16)], "meeting.wav", { type: "audio/wav" });

		fireEvent.change(screen.getByTestId("new-transcription-session-file"), { target: { files: [recording] } });
		fireEvent.click(screen.getByRole("radio", { name: "Microphone" }));
		fireEvent.click(screen.getByTestId("new-transcription-session-submit"));

		expect(onSubmit).toHaveBeenCalledTimes(1);
		const values = onSubmit.mock.calls[0]?.[0] as { sourceKind: string; file: File | null; title: string };
		expect(values.sourceKind).toBe("Microphone");
		expect(values.file).toBeNull();
		expect(values.title).not.toBe("meeting.wav");
	});

	// Re-picking the source and the microphone on every session is the kind of repeated ceremony the operator should
	// only pay once.
	it("remembers the source it was last submitted with", () => {
		renderDialog();

		fireEvent.click(screen.getByRole("radio", { name: "System audio" }));
		fireEvent.click(screen.getByTestId("new-transcription-session-submit"));

		expect(useTranscriptionCaptureStore.getState().lastSourceKind).toBe("SystemAudio");
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
			channelAttribution: false,
			file,
			deviceId: null,
			processId: null,
		});
	});

	// The capture window is the node's to choose (its default, 5 s): no source offers the control any more.
	it("offers no capture-window control on any source", () => {
		renderDialog();

		for (const label of ["File", "Microphone", "System audio", "Microphone + system"]) {
			fireEvent.click(screen.getByRole("radio", { name: label }));
			expect(screen.queryByTestId("new-transcription-session-max-window")).toBeNull();
		}
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

	// Per-application capture exists only on Windows 10 build 20348 and later. An option that is present and refused
	// teaches the operator nothing, so the node's own flag decides whether it is offered at all.
	it("offers no application source where the node cannot capture one", async () => {
		let runtimeReads = 0;
		server.use(
			http.get(localApiPath("transcription/runtime"), () => {
				runtimeReads += 1;
				return HttpResponse.json(runtimeBody(false));
			}),
		);
		renderDialog();

		// Waiting for the answer matters: asserting the option's absence before the flag has arrived would pass on
		// every box, supported or not.
		await waitFor(() => expect(runtimeReads).toBeGreaterThan(0));
		await waitFor(() => expect(screen.queryByRole("radio", { name: applicationSource })).toBeNull());
	});

	it("offers the application source, reachable, once the node reports support", async () => {
		server.use(runtimeRoute(true));
		renderDialog();

		expect(await screen.findByRole("radio", { name: applicationSource })).toHaveProperty("disabled", false);
	});

	// R21a: WASAPI captures the target process AND its descendants — there is no "only this application" mode — so the
	// copy states the one behaviour instead of the dialog offering a scope the platform does not have.
	it("says the application's child processes are captured too", async () => {
		server.use(runtimeRoute(true));
		renderDialog();

		fireEvent.click(await screen.findByRole("radio", { name: applicationSource }));

		expect(screen.getByText("Captures this application and its child processes.")).toBeDefined();
	});

	// M1: `lastSourceKind` is persisted but the option list is not, so a box that stopped reporting support — or a
	// runtime read that has not answered — used to leave the SegmentedControl on a value absent from its own data:
	// nothing highlighted, an empty picker, and Create disabled with nothing explaining why.
	it("falls back to a visible source when the remembered one is no longer offered", async () => {
		useTranscriptionCaptureStore.setState({ lastSourceKind: "ApplicationProcess" });
		let runtimeReads = 0;
		server.use(
			http.get(localApiPath("transcription/runtime"), () => {
				runtimeReads += 1;
				return HttpResponse.json(runtimeBody(false));
			}),
		);
		renderDialog();

		await waitFor(() => expect(runtimeReads).toBeGreaterThan(0));
		// File is the fallback, so its own controls are on screen and one segment is actually selected.
		expect(await screen.findByTestId("new-transcription-session-file")).toBeDefined();
		expect(screen.getByRole("radio", { name: "File" })).toHaveProperty("checked", true);
		expect(screen.queryByTestId("new-transcription-session-process")).toBeNull();
	});

	// M3: the application the operator picked can exit while the dialog is open. A refresh then returns a list it is
	// not in, and a stored pid would leave a blank Select over a live selection — creating the row against a process
	// nobody can capture, which only fails much later, at Start.
	it("drops the chosen application when a refresh no longer lists it", async () => {
		let listed = [{ pid: 4242, name: "Zoom Meetings", hasAudio: true }];
		server.use(
			runtimeRoute(true),
			http.get(localApiPath("transcription/capture/processes"), () => HttpResponse.json({ supported: true, processes: listed })),
		);
		renderDialog();

		fireEvent.click(await screen.findByRole("radio", { name: applicationSource }));
		fireEvent.click(await screen.findByTestId("new-transcription-session-process"));
		fireEvent.click(await screen.findByRole("option", { name: "Zoom Meetings", hidden: true }));
		expect(screen.getByTestId("new-transcription-session-submit")).toHaveProperty("disabled", false);

		listed = [];
		fireEvent.click(screen.getByTestId("new-transcription-session-process-refresh"));

		await waitFor(() => expect(screen.getByTestId("new-transcription-session-submit")).toHaveProperty("disabled", true));
	});

	// The pid is the only thing this source cannot be started without, and the create request never carries it — it
	// reaches the node later, as the capture/process body — so it has to ride out of the dialog on the submitted values.
	it("lists the applications the node reports and submits the chosen one", async () => {
		server.use(runtimeRoute(true), processesRoute([{ pid: 4242, name: "Zoom Meetings", hasAudio: true }]));
		const onSubmit = renderDialog();

		fireEvent.click(await screen.findByRole("radio", { name: applicationSource }));
		// Nothing is picked yet, so there is nothing to start: submitting now would create a row the session view
		// could never capture for.
		expect(screen.getByTestId("new-transcription-session-submit")).toHaveProperty("disabled", true);

		fireEvent.click(await screen.findByTestId("new-transcription-session-process"));
		fireEvent.click(await screen.findByRole("option", { name: "Zoom Meetings", hidden: true }));
		fireEvent.click(screen.getByTestId("new-transcription-session-submit"));

		expect(onSubmit).toHaveBeenCalledTimes(1);
		const values = onSubmit.mock.calls[0]?.[0] as { sourceKind: string; processId: number | null; file: File | null };
		expect(values.sourceKind).toBe("ApplicationProcess");
		expect(values.processId).toBe(4242);
		expect(values.file).toBeNull();
	});

	// A fresh node has no weights, and the runtime only says so as a raw exception once the recording has already
	// been uploaded. The catalogue answers it before any of that, so the dialog names it and points at the fix.
	it("warns that no transcription model is installed and names where to get one", async () => {
		server.use(modelsRoute(false));
		renderDialog();

		const warning = await screen.findByTestId("new-transcription-session-no-model");
		expect(warning.textContent).toContain("No transcription model is installed on this node yet");
		expect(warning.textContent).toContain("Whisper runtime");
	});

	it("says nothing about models once one is installed", async () => {
		renderDialog();

		await screen.findByTestId("new-transcription-session-source");
		await waitFor(() => {
			expect(screen.queryByTestId("new-transcription-session-no-model")).toBeNull();
		});
	});
});
