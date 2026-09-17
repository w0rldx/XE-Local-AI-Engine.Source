// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { TranscriptionPage } from "@/features/transcription/pages/TranscriptionPage";
import { useTranscriptionCaptureStore } from "@/features/transcription/stores/TranscriptionCaptureStore";
import { http, HttpResponse } from "msw";

import { domainErrorRoute, jsonRoute, localApiPath } from "@/test/msw/Handlers";
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

function runtimeRoutes() {
	return [
		jsonRoute("get", "transcription/runtime", {
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
			processCaptureSupported: true,
			managedRuntime: null,
			activity: {
				activeTranscriptionCount: 0,
				spawnReadinessCount: 0,
				residentProcessCount: 1,
				mutationReserved: false,
				evictionReserved: false,
				isBusy: false,
			},
		}),
		jsonRoute("get", "transcription/models", { models: [], selectedModelId: "base", recommendedModelId: "base" }),
	];
}

function summary(overrides: Record<string, unknown> = {}) {
	return {
		id: sessionId,
		title: "Standup recording",
		status: "Completed",
		sourceKind: "File",
		modelId: "base",
		segmentCount: 3,
		createdAtUtc: 1_700_000_000_000,
		updatedAtUtc: 1_700_000_060_000,
		...overrides,
	};
}

function detail() {
	return {
		session: summary(),
		segments: [],
		config: { languageMode: "auto", translate: false, maxWindowSeconds: 5, channelAttribution: false },
	};
}

// Mantine's Modal mounts through a transition, and its FileInput renders a button plus a hidden native input — so
// the dialog is awaited first and the native input is then reached through the document, not the render container.
async function stageFile(name = "standup.wav"): Promise<void> {
	await screen.findByTestId("new-transcription-session-file");
	const nativeFileInput = document.querySelector('input[type="file"]');
	if (nativeFileInput === null) {
		throw new Error("The dialog rendered no native file input to stage a recording on.");
	}
	fireEvent.change(nativeFileInput, { target: { files: [new File(["audio"], name, { type: "audio/wav" })] } });
}

function renderPage() {
	return renderWithProviders(
		<ConfirmProvider>
			<TranscriptionPage />
		</ConfirmProvider>,
	);
}

describe("TranscriptionPage", () => {
	// The dialog opens on whatever was picked last time and the store is persisted, so the remembered source and the
	// per-session devices are reset per test rather than carried from the one before.
	beforeEach(() => {
		navigate.mockClear();
		useTranscriptionCaptureStore.setState({ lastSourceKind: "File", deviceIdBySession: {}, processIdBySession: {} });
	});

	afterEach(() => {
		cleanup();
	});

	// With R25a there is no defaultValue to fall back on, so a missing bundle entry would surface here as the raw key.
	it("introduces itself with the English page header, not a translation key", async () => {
		server.use(jsonRoute("get", "transcription/sessions", { items: [], totalCount: 0 }), ...runtimeRoutes());
		renderPage();

		expect(await screen.findByRole("heading", { level: 1, name: "Transcription" })).toBeDefined();
	});

	it("offers a create call to action when there are no sessions", async () => {
		server.use(jsonRoute("get", "transcription/sessions", { items: [], totalCount: 0 }), ...runtimeRoutes());
		renderPage();

		await screen.findByTestId("transcription-sessions-empty");
		expect(screen.getByTestId("transcription-empty-create")).toBeDefined();
		expect(screen.queryByTestId("transcription-session-list")).toBeNull();
	});

	it("renders the session history from the list endpoint", async () => {
		server.use(jsonRoute("get", "transcription/sessions", { items: [summary()], totalCount: 1 }), ...runtimeRoutes());
		renderPage();

		const card = await screen.findByTestId(`transcription-session-card-${sessionId}`);
		expect(card.textContent).toContain("Standup recording");
	});

	it("renders an inline alert with a retry when the list fails", async () => {
		server.use(
			domainErrorRoute("get", "transcription/sessions", 500, { detail: "the store is unavailable" }),
			...runtimeRoutes(),
		);
		renderPage();

		const alert = await screen.findByTestId("transcription-sessions-error");
		expect(alert.textContent).toContain("the store is unavailable");
		expect(screen.getByTestId("transcription-sessions-retry")).toBeDefined();
	});

	it("creates the session, uploads the file and opens the finished transcript", async () => {
		server.use(
			jsonRoute("get", "transcription/sessions", { items: [], totalCount: 0 }),
			jsonRoute("post", "transcription/sessions", detail()),
			jsonRoute("post", `transcription/sessions/${sessionId}/file`, detail()),
			...runtimeRoutes(),
		);
		renderPage();

		fireEvent.click(await screen.findByTestId("transcription-create"));
		await stageFile();
		fireEvent.click(screen.getByTestId("new-transcription-session-submit"));

		await waitFor(() => {
			expect(navigate).toHaveBeenCalledWith({ to: "/transcription/$sessionId", params: { sessionId } });
		});
	});

	// M3: the device is never sent to the node, so this store is the only record of which microphone a session was
	// configured for — and it has to be keyed by the session, not by the operator. A single global slot let the
	// session created second decide what the session created first captured from.
	it("remembers the microphone against the session it created, not globally", async () => {
		server.use(
			jsonRoute("get", "transcription/sessions", { items: [], totalCount: 0 }),
			jsonRoute("post", "transcription/sessions", detail()),
			...runtimeRoutes(),
		);
		renderPage();

		fireEvent.click(await screen.findByTestId("transcription-create"));
		// Mantine's Modal mounts through a transition, so the source control is awaited rather than assumed.
		fireEvent.click(await screen.findByRole("radio", { name: "Microphone" }));
		fireEvent.click(screen.getByTestId("new-transcription-session-submit"));

		await waitFor(() => {
			expect(navigate).toHaveBeenCalledWith({ to: "/transcription/$sessionId", params: { sessionId } });
		});
		expect(useTranscriptionCaptureStore.getState().deviceIdBySession).toEqual({ [sessionId]: null });
	});

	// The pid is chosen in the dialog but only reaches the node once the session is live, so the create path is the one
	// place that can tie it to the new session id — the same point the microphone is remembered at.
	it("remembers the application against the session it created", async () => {
		server.use(
			jsonRoute("get", "transcription/sessions", { items: [], totalCount: 0 }),
			jsonRoute("post", "transcription/sessions", detail()),
			jsonRoute("get", "transcription/capture/processes", {
				supported: true,
				processes: [{ pid: 4242, name: "Zoom Meetings", hasAudio: true }],
			}),
			...runtimeRoutes(),
		);
		renderPage();

		fireEvent.click(await screen.findByTestId("transcription-create"));
		fireEvent.click(await screen.findByRole("radio", { name: "Application audio (Windows)" }));
		fireEvent.click(await screen.findByTestId("new-transcription-session-process"));
		fireEvent.click(await screen.findByRole("option", { name: "Zoom Meetings", hidden: true }));
		fireEvent.click(screen.getByTestId("new-transcription-session-submit"));

		await waitFor(() => {
			expect(navigate).toHaveBeenCalledWith({ to: "/transcription/$sessionId", params: { sessionId } });
		});
		expect(useTranscriptionCaptureStore.getState().processIdBySession).toEqual({ [sessionId]: 4242 });
	});

	// The 415 carries the readable-container list; showing only "unsupported" would leave the operator guessing which
	// formats this node can actually take.
	it("names the readable containers when the upload refuses the file", async () => {
		server.use(
			jsonRoute("get", "transcription/sessions", { items: [], totalCount: 0 }),
			jsonRoute("post", "transcription/sessions", detail()),
			domainErrorRoute("post", `transcription/sessions/${sessionId}/file`, 415, {
				reason: "unsupported-container",
				message: "This node cannot read a Matroska container.",
				detectedContainer: "Matroska",
				supportedContainers: ["Wav", "Mp3"],
				ffmpegRequired: false,
			}),
			...runtimeRoutes(),
		);
		renderPage();

		fireEvent.click(await screen.findByTestId("transcription-create"));
		await stageFile("meeting.mkv");
		fireEvent.click(screen.getByTestId("new-transcription-session-submit"));

		const error = await screen.findByTestId("new-transcription-session-error");
		expect(error.textContent).toContain("Wav, Mp3");
		expect(error.textContent).not.toContain("ffmpeg");
		expect(navigate).not.toHaveBeenCalled();
	});

	// The recording is never stored, so the transcript is the only copy — deleting it is irreversible and must not
	// happen on a single mis-click of an icon that sits inside the card's own navigation target.
	it("deletes a session only after the confirmation is accepted", async () => {
		let deleteRequests = 0;
		server.use(
			jsonRoute("get", "transcription/sessions", { items: [summary()], totalCount: 1 }),
			http.delete(localApiPath(`transcription/sessions/${sessionId}`), () => {
				deleteRequests += 1;
				return new HttpResponse(null, { status: 204 });
			}),
			...runtimeRoutes(),
		);
		renderPage();

		fireEvent.click(await screen.findByTestId(`transcription-session-delete-${sessionId}`));
		fireEvent.click(await screen.findByTestId("confirm-cancel"));
		expect(deleteRequests).toBe(0);

		fireEvent.click(screen.getByTestId(`transcription-session-delete-${sessionId}`));
		fireEvent.click(await screen.findByTestId("confirm-accept"));
		await waitFor(() => {
			expect(deleteRequests).toBe(1);
		});
	});

	// The two 415 cases need different actions: one says "re-export", the other says "install ffmpeg and the engine
	// will convert it for you". A shared sentence would send the ffmpeg case down the wrong path.
	it("tells the operator to install ffmpeg when that is what the refusal was about", async () => {
		server.use(
			jsonRoute("get", "transcription/sessions", { items: [], totalCount: 0 }),
			jsonRoute("post", "transcription/sessions", detail()),
			domainErrorRoute("post", `transcription/sessions/${sessionId}/file`, 415, {
				reason: "ffmpeg-required",
				message: "ffmpeg is not installed.",
				detectedContainer: "Matroska",
				supportedContainers: ["Wav", "Mp3"],
				ffmpegRequired: true,
			}),
			...runtimeRoutes(),
		);
		renderPage();

		fireEvent.click(await screen.findByTestId("transcription-create"));
		await stageFile("meeting.mkv");
		fireEvent.click(screen.getByTestId("new-transcription-session-submit"));

		const error = await screen.findByTestId("new-transcription-session-error");
		expect(error.textContent).toContain("ffmpeg");
		expect(error.textContent).toContain("Wav, Mp3");
	});

	// Every line of the runtime card states a fact about the runtime, so before the status loads it must state none:
	// an unloaded card once reported "voice-activity detection is not installed" and an empty model name, which reads
	// as a broken install rather than as a pending fetch.
	it("claims nothing about the runtime before its status has loaded", () => {
		server.use(jsonRoute("get", "transcription/sessions", { items: [], totalCount: 0 }), ...runtimeRoutes());
		renderPage();

		expect(screen.getByTestId("transcription-runtime-loading")).toBeDefined();
		expect(screen.queryByTestId("transcription-runtime-state")).toBeNull();
		expect(screen.queryByTestId("transcription-runtime-eject")).toBeNull();
	});
});
