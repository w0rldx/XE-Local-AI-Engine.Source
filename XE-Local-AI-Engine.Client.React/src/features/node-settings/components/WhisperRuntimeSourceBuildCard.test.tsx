// @vitest-environment jsdom

import type { QueryClient } from "@tanstack/react-query";
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import en from "@/locales/en.json";
import { domainErrorRoute, jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// The one UI for the only GPU transcription path a Linux + NVIDIA node has: upstream ships no Linux CUDA whisper
// binary, so without this card that box is on the CPU permanently. What is pinned here is the whole contract of the
// five endpoints that had no caller — the prerequisite checklist naming what is missing and refusing the build for
// it, a running build's phase and log (polled, because this lane has no hub), cancel, a failure and the interrupted-
// build recovery the node reports after a restart, the adopted runtime with its confirmed removal, and the typed 409
// refusals surfaced in the server's own words rather than as a generic error.

const { toastMock } = vi.hoisted(() => ({
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { WhisperRuntimeSourceBuildCard } from "@/features/node-settings/components/WhisperRuntimeSourceBuildCard";

const copy = en.pages.nodeSettings.transcriptionRuntime.sourceBuild;

interface ActivityOverrides {
	readonly activeTranscriptionCount?: number;
	readonly spawnReadinessCount?: number;
	readonly residentProcessCount?: number;
	readonly isBusy?: boolean;
}

function activity(overrides: ActivityOverrides = {}) {
	return {
		activeTranscriptionCount: overrides.activeTranscriptionCount ?? 0,
		spawnReadinessCount: overrides.spawnReadinessCount ?? 0,
		residentProcessCount: overrides.residentProcessCount ?? 0,
		mutationReserved: false,
		evictionReserved: false,
		isBusy: overrides.isBusy ?? false,
	};
}

function managedRuntime(overrides: { validity?: "active" | "invalid"; invalidReason?: string | null } = {}) {
	return {
		validity: overrides.validity ?? "active",
		desiredBackend: "cuda",
		sourceRepository: "https://github.com/ggml-org/whisper.cpp",
		sourceCommit: "927cfce3d1a1b2c3d4e5f60718293a4b5c6d7e8f",
		sourceSelection: "official",
		sourceRevisionMode: "enginePinned",
		sourceRequestedCommit: null,
		installedAtUtc: 1,
		invalidReason: overrides.invalidReason ?? null,
	};
}

function runtimeRoute(overrides: { managed?: ReturnType<typeof managedRuntime> | null; activity?: ActivityOverrides } = {}) {
	return jsonRoute("get", "transcription/runtime", {
		enabled: true,
		state: "stopped",
		backend: null,
		binarySource: null,
		binaryVersion: null,
		loadedModelId: null,
		selectedModelId: null,
		recommendedModelId: "base",
		supportsTranscode: true,
		idleTimeoutMinutes: 10,
		vadInstalled: true,
		processCaptureSupported: false,
		managedRuntime: overrides.managed ?? null,
		activity: activity(overrides.activity),
	});
}

function prerequisitesRoute(items: readonly { key: string; satisfied: boolean; detail: string }[], canBuild: boolean) {
	return jsonRoute("get", "transcription/runtime/source-build/prerequisites", { backend: "cuda", items, canBuild });
}

const satisfiedPrerequisites = [
	{ key: "os-is-linux", satisfied: true, detail: "Linux host detected." },
	{ key: "nvcc", satisfied: true, detail: "nvcc: Cuda compilation tools, release 13.3" },
	{ key: "cmake", satisfied: true, detail: "cmake version 3.31.6" },
];

function statusRoute(
	overrides: {
		phase?: string;
		isRunning?: boolean;
		terminal?: boolean;
		logLines?: readonly string[];
		sanitizedError?: string | null;
		withBuild?: boolean;
	} = {},
) {
	return jsonRoute("get", "transcription/runtime/source-build/status", {
		phase: overrides.phase ?? "idle",
		isRunning: overrides.isRunning ?? false,
		terminal: overrides.terminal ?? false,
		logStartSequence: 0,
		logLines: overrides.logLines ?? [],
		sanitizedError: overrides.sanitizedError ?? null,
		currentBuild:
			overrides.withBuild === true
				? {
						buildId: "11111111-1111-4111-8111-111111111111",
						backend: "cuda",
						source: "official",
						repository: "https://github.com/ggml-org/whisper.cpp",
						revisionMode: "enginePinned",
						requestedCommit: null,
						resolvedCommit: null,
					}
				: null,
		startedAtUtc: null,
		completedAtUtc: null,
	});
}

/**
 * Opens the build form. Everything that measures the toolchain lives behind this disclosure, because the probe
 * behind it spawns a compiler-toolchain's worth of child processes and the three cards render unconditionally.
 */
async function openBuildForm(): Promise<void> {
	fireEvent.click(await screen.findByTestId("whisper-source-build-form-toggle"));
}

/** Cache entries for the prerequisite probe, whatever backend variant they were keyed under. */
function probeQueries(queryClient: QueryClient) {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return queryClient.getQueryCache().findAll({ queryKey: [{ _id: "getWhisperCppSourceBuildPrerequisites" }] });
}

/** The 409 shape every blocked transcription-runtime mutation returns: a reason code and the server's sentence. */
function blockedRoute(path: string, reason: string, message: string) {
	return domainErrorRoute("post", path, 409, { reason, message, activity: activity({ residentProcessCount: 1, isBusy: true }) });
}

setupMswServer();

describe("WhisperRuntimeSourceBuildCard", () => {
	beforeEach(() => {
		toastMock.error.mockClear();
	});

	afterEach(() => {
		cleanup();
		// The request spies below are file-scoped listeners on the shared MSW singleton; without this they survive into
		// the next test and count its requests too.
		server.events.removeAllListeners();
		useDeveloperModeStore.getState().actions.setDeveloperMode(false);
	});

	it("names the missing toolchain entry and refuses the build for it", async () => {
		server.use(
			runtimeRoute(),
			statusRoute(),
			prerequisitesRoute(
				[
					{ key: "os-is-linux", satisfied: true, detail: "Linux host detected." },
					{ key: "nvcc", satisfied: false, detail: "NVIDIA CUDA compiler (nvcc) is not available." },
					{ key: "free-disk", satisfied: false, detail: "At least 15 GiB of free disk space is required." },
				],
				false,
			),
		);
		renderWithProviders(<WhisperRuntimeSourceBuildCard />);
		await openBuildForm();

		// The row exists for each probe, and the unsatisfied ones read as missing rather than merely being absent.
		expect(await screen.findByText(copy.prerequisites.nvcc)).toBeTruthy();
		expect(screen.getAllByText(copy.prerequisiteAvailability.missing).length).toBe(2);
		await waitFor(() => expect((screen.getByRole("button", { name: copy.build }) as HTMLButtonElement).disabled).toBe(true));
		// The disk figure the service enforces is stated up front, because the refusal arrives after a long wait.
		expect(screen.getByText(copy.buildCost)).toBeTruthy();
	});

	it("starts the engine-pinned CUDA build the node defaults to", async () => {
		const started = vi.fn();
		server.use(
			runtimeRoute(),
			statusRoute(),
			prerequisitesRoute(satisfiedPrerequisites, true),
			jsonRoute("post", "transcription/runtime/source-build", {
				started: true,
				status: {
					phase: "cloning",
					isRunning: true,
					terminal: false,
					logStartSequence: 0,
					logLines: [],
					sanitizedError: null,
					currentBuild: null,
					startedAtUtc: null,
					completedAtUtc: null,
				},
			}),
		);
		server.events.on("request:start", ({ request }) => {
			if (request.method === "POST" && request.url.endsWith("/source-build")) {
				started();
			}
		});
		renderWithProviders(<WhisperRuntimeSourceBuildCard />);
		await openBuildForm();

		expect((await screen.findByTestId("whisper-source-build-revision-behavior")).textContent).toBe(
			copy.revisionBehavior.enginePinned,
		);
		const build = await screen.findByRole("button", { name: copy.build });
		await waitFor(() => expect((build as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(build);

		await waitFor(() => expect(started).toHaveBeenCalled());
		expect(toastMock.error).not.toHaveBeenCalled();
	});

	it("renders the running phase and the polled log tail, and cancels", async () => {
		const cancelled = vi.fn();
		server.use(
			runtimeRoute(),
			prerequisitesRoute(satisfiedPrerequisites, true),
			statusRoute({
				phase: "building",
				isRunning: true,
				logLines: ["-- Configuring done", "[ 42%] Building CXX object whisper.cpp.o"],
				withBuild: true,
			}),
			jsonRoute("post", "transcription/runtime/source-build/cancel", {
				phase: "cancelled",
				isRunning: false,
				terminal: true,
				logStartSequence: 0,
				logLines: [],
				sanitizedError: null,
				currentBuild: null,
				startedAtUtc: null,
				completedAtUtc: null,
			}),
		);
		server.events.on("request:start", ({ request }) => {
			if (request.method === "POST" && request.url.endsWith("/source-build/cancel")) {
				cancelled();
			}
		});
		renderWithProviders(<WhisperRuntimeSourceBuildCard />);

		expect((await screen.findByTestId("cuda-build-phase")).textContent).toBe("building");
		expect((await screen.findByTestId("cuda-build-log-content")).textContent).toContain("Building CXX object");
		fireEvent.click(await screen.findByRole("button", { name: copy.cancel }));

		await waitFor(() => expect(cancelled).toHaveBeenCalled());
	});

	it("shows the sanitized failure reason and leaves the build available to retry", async () => {
		server.use(
			runtimeRoute(),
			prerequisitesRoute(satisfiedPrerequisites, true),
			statusRoute({
				phase: "failed",
				terminal: true,
				sanitizedError: "The build failed while compiling the CUDA backend.",
			}),
		);
		renderWithProviders(<WhisperRuntimeSourceBuildCard />);

		expect(await screen.findByText("The build failed while compiling the CUDA backend.")).toBeTruthy();
		const build = await screen.findByRole("button", { name: copy.build });
		await waitFor(() => expect((build as HTMLButtonElement).disabled).toBe(false));
	});

	// A node restarted mid-build reports that as a terminal `failed` phase carrying the recovery sentence, not as a
	// build still running: without this the card would wait forever on a build whose temporary files are already gone.
	it("reports a build the node interrupted and recovered", async () => {
		server.use(
			runtimeRoute(),
			prerequisitesRoute(satisfiedPrerequisites, true),
			statusRoute({
				phase: "failed",
				terminal: true,
				sanitizedError: "A previously interrupted source build was recovered and its temporary files were removed.",
			}),
		);
		renderWithProviders(<WhisperRuntimeSourceBuildCard />);

		expect(
			await screen.findByText("A previously interrupted source build was recovered and its temporary files were removed."),
		).toBeTruthy();
		expect(screen.queryByTestId("cuda-build-log")).toBeNull();
	});

	it("says what a finished build means and removes the adopted runtime only after confirmation", async () => {
		const removed = vi.fn();
		server.use(
			runtimeRoute({ managed: managedRuntime() }),
			prerequisitesRoute(satisfiedPrerequisites, true),
			statusRoute({ phase: "completed", terminal: true }),
			jsonRoute("post", "transcription/runtime/source-build/remove", {
				phase: "completed",
				isRunning: false,
				terminal: true,
				logStartSequence: 0,
				logLines: [],
				sanitizedError: null,
				currentBuild: null,
				startedAtUtc: null,
				completedAtUtc: null,
			}),
		);
		server.events.on("request:start", ({ request }) => {
			if (request.method === "POST" && request.url.endsWith("/source-build/remove")) {
				removed();
			}
		});
		renderWithProviders(<WhisperRuntimeSourceBuildCard />);

		expect((await screen.findByTestId("whisper-source-build-succeeded")).textContent).toBe(copy.succeeded);
		expect(await screen.findByTestId("managed-whisper-runtime-status")).toBeTruthy();
		expect(screen.getByText("Active cuda managed transcription runtime")).toBeTruthy();

		// The destructive action is a two-step: the button opens the confirmation, nothing is sent until it is taken.
		fireEvent.click(await screen.findByRole("button", { name: copy.remove }));
		expect(await screen.findByText(copy.removeConfirmBody)).toBeTruthy();
		expect(removed).not.toHaveBeenCalled();

		fireEvent.click(screen.getByRole("button", { name: copy.removeConfirm }));
		await waitFor(() => expect(removed).toHaveBeenCalled());
	});

	it("surfaces the node's own refusal when the runtime is busy", async () => {
		server.use(
			runtimeRoute({ activity: { residentProcessCount: 1, isBusy: true } }),
			prerequisitesRoute(satisfiedPrerequisites, true),
			statusRoute(),
			blockedRoute(
				"transcription/runtime/source-build",
				"runtime-busy",
				"Wait for active transcriptions and transcription-runtime processes to finish before starting the build.",
			),
			jsonRoute("post", "transcription/runtime/eject", { ejected: true }),
		);
		renderWithProviders(<WhisperRuntimeSourceBuildCard />);
		await openBuildForm();

		// A resident daemon holds the mutation reservation the build needs, so the card says so and offers the eject
		// that is the only way out of the refusal rather than leaving the operator to find it on another page.
		expect(await screen.findByTestId("whisper-runtime-activity")).toBeTruthy();
		const eject = await screen.findByRole("button", { name: copy.eject });
		expect((eject as HTMLButtonElement).disabled).toBe(false);
		await waitFor(() => expect((screen.getByRole("button", { name: copy.build }) as HTMLButtonElement).disabled).toBe(true));
	});

	it("reports a start the node refuses in the server's own words", async () => {
		server.use(
			runtimeRoute(),
			prerequisitesRoute(satisfiedPrerequisites, true),
			statusRoute(),
			blockedRoute(
				"transcription/runtime/source-build",
				"already-building",
				"A whisper.cpp source build is already in progress.",
			),
		);
		renderWithProviders(<WhisperRuntimeSourceBuildCard />);
		await openBuildForm();

		const build = await screen.findByRole("button", { name: copy.build });
		await waitFor(() => expect((build as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(build);

		await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("A whisper.cpp source build is already in progress."));
	});

	// The owner ungated all three source-build cards on 2026-09-19: the routes behind them are Operator-authorized and
	// inert until Build is pressed, and hiding the only GPU-transcription path behind a mode the target operator has no
	// reason to enable made it unfindable. Developer Mode must therefore change nothing here at all.
	it("renders in full with developer mode off", async () => {
		useDeveloperModeStore.getState().actions.setDeveloperMode(false);
		server.use(runtimeRoute(), statusRoute(), prerequisitesRoute(satisfiedPrerequisites, true));
		renderWithProviders(<WhisperRuntimeSourceBuildCard />);
		await openBuildForm();

		expect(await screen.findByTestId("whisper-runtime-source-build-card")).toBeTruthy();
		expect(await screen.findByText(copy.prerequisites.nvcc)).toBeTruthy();
		expect(screen.getByRole("combobox", { name: copy.backend })).toBeTruthy();
		await waitFor(() => expect((screen.getByRole("button", { name: copy.build }) as HTMLButtonElement).disabled).toBe(false));
	});

	// The fail-closed tombstone's in-app exit. It no longer depends on a mode, but the record must still state why it
	// is invalid and leave Remove usable — that part was always about recovery, not about Developer Mode.
	it("exposes removal of an invalid runtime with its reason", async () => {
		useDeveloperModeStore.getState().actions.setDeveloperMode(false);
		server.use(
			runtimeRoute({
				managed: managedRuntime({ validity: "invalid", invalidReason: "The managed binary failed its smoke test." }),
			}),
			statusRoute(),
			prerequisitesRoute(satisfiedPrerequisites, true),
		);
		renderWithProviders(<WhisperRuntimeSourceBuildCard />);

		expect(await screen.findByText("Invalid cuda managed transcription runtime")).toBeTruthy();
		expect(screen.getByText("The managed binary failed its smoke test.")).toBeTruthy();
		expect((screen.getByRole("button", { name: copy.remove }) as HTMLButtonElement).disabled).toBe(false);
		// A rebuild is now offered beside the removal — the record is invalid, not the toolchain.
		expect(await screen.findByRole("button", { name: copy.rebuild })).toBeTruthy();
	});

	// The reason the disclosure exists. Each prerequisite GET really runs the toolchain — ~8 child processes for this
	// card alone — and three of these cards render on Node Settings for every operator. A bare "no request yet" right
	// after render would pass while one was in flight, so this also asserts the query never even entered the cache and
	// that nothing at all is fetching once the two cheap reads have settled.
	it("probes nothing on mount while the build form is closed", async () => {
		let probes = 0;
		server.use(runtimeRoute(), statusRoute(), prerequisitesRoute(satisfiedPrerequisites, true));
		server.events.on("request:start", ({ request }) => {
			if (request.url.includes("/source-build/prerequisites")) {
				probes += 1;
			}
		});
		const { queryClient } = renderWithProviders(<WhisperRuntimeSourceBuildCard />);

		// The card is fully painted from status + runtime, so a probe that was going to fire has had its chance.
		expect(await screen.findByTestId("whisper-source-build-form-toggle")).toBeTruthy();
		await waitFor(() => expect(queryClient.isFetching()).toBe(0));
		expect(probes).toBe(0);
		// A DISABLED useQuery still registers a cache entry, so "no entry" would be the wrong claim. What must hold is
		// that the entry never fetched: idle fetch status and no data has ever landed in it.
		for (const query of probeQueries(queryClient)) {
			expect(query.state.fetchStatus).toBe("idle");
			expect(query.state.dataUpdatedAt).toBe(0);
		}
		// `keepMounted={false}`: a closed form holds no focusable inputs either.
		expect(screen.queryByRole("combobox", { name: copy.backend })).toBeNull();

		fireEvent.click(screen.getByTestId("whisper-source-build-form-toggle"));

		expect(await screen.findByText(copy.prerequisites.nvcc)).toBeTruthy();
		await waitFor(() => expect(queryClient.isFetching()).toBe(0));
		expect(probes).toBe(1);
		expect(probeQueries(queryClient).some((query) => query.state.dataUpdatedAt > 0)).toBe(true);
	});

	it("names the collapsed form for a screen reader and points it at the region it controls", async () => {
		server.use(runtimeRoute(), statusRoute(), prerequisitesRoute(satisfiedPrerequisites, true));
		renderWithProviders(<WhisperRuntimeSourceBuildCard />);

		const toggle = await screen.findByRole("button", { name: en.components.sourceBuild.buildFromSource });
		expect(toggle.getAttribute("aria-expanded")).toBe("false");
		// The region has to exist while collapsed, or `aria-controls` points at nothing in the one state it describes.
		const controlled = toggle.getAttribute("aria-controls");
		expect(controlled).toBeTruthy();
		expect(document.getElementById(controlled as string)).not.toBeNull();

		fireEvent.click(toggle);
		await waitFor(() => expect(toggle.getAttribute("aria-expanded")).toBe("true"));
	});

	// A running build, a failure and an invalid record each mean the form IS the next thing the operator needs, so it
	// opens itself — decided from the status reads alone, never by probing.
	it.each([
		["a build is running", () => statusRoute({ phase: "building", isRunning: true, withBuild: true }), undefined],
		["a build failed", () => statusRoute({ phase: "failed", terminal: true, sanitizedError: "The build failed." }), undefined],
		[
			"the managed record is invalid",
			() => statusRoute(),
			() => managedRuntime({ validity: "invalid", invalidReason: "The managed binary failed its smoke test." }),
		],
	])("expands the build form on its own when %s", async (_case, status, managed) => {
		server.use(runtimeRoute({ managed: managed?.() ?? null }), status(), prerequisitesRoute(satisfiedPrerequisites, true));
		renderWithProviders(<WhisperRuntimeSourceBuildCard />);

		expect(await screen.findByText(copy.prerequisites.nvcc)).toBeTruthy();
		expect((await screen.findByTestId("whisper-source-build-form-toggle")).getAttribute("aria-expanded")).toBe("true");
	});

	// Rule: do not collapse under the operator. The status tick where a build stops running must not pull the form —
	// their retry — out from under them.
	it("keeps the form open after the build that opened it stops running", async () => {
		let running = true;
		server.use(
			runtimeRoute(),
			prerequisitesRoute(satisfiedPrerequisites, true),
			http.get(localApiPath("transcription/runtime/source-build/status"), () =>
				HttpResponse.json({
					phase: running ? "building" : "cancelled",
					isRunning: running,
					terminal: !running,
					logStartSequence: 0,
					logLines: [],
					sanitizedError: null,
					currentBuild: null,
					startedAtUtc: null,
					completedAtUtc: null,
				}),
			),
		);
		const { queryClient } = renderWithProviders(<WhisperRuntimeSourceBuildCard />);

		const toggle = await screen.findByTestId("whisper-source-build-form-toggle");
		await waitFor(() => expect(toggle.getAttribute("aria-expanded")).toBe("true"));

		running = false;
		await queryClient.invalidateQueries();
		await waitFor(() => expect(screen.queryByTestId("cuda-build-log")).toBeNull());
		expect(toggle.getAttribute("aria-expanded")).toBe("true");
	});

	it("requires the trust acknowledgement before a custom fork can be built", async () => {
		server.use(runtimeRoute(), statusRoute(), prerequisitesRoute(satisfiedPrerequisites, true));
		renderWithProviders(<WhisperRuntimeSourceBuildCard />);
		await openBuildForm();

		// Both queries must have settled first: a re-render from one landing mid-interaction closes the open dropdown.
		await waitFor(() => expect((screen.getByRole("button", { name: copy.build }) as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(screen.getByRole("combobox", { name: copy.source }));
		// `hidden: true`: jsdom has no layout engine, so Mantine's popover dropdown never loses its `display: none`
		// even once open — the same accommodation every other Select test in this repo makes.
		fireEvent.click(await screen.findByRole("option", { name: copy.sources.custom, hidden: true }));
		fireEvent.change(screen.getByLabelText(copy.repository), {
			target: { value: "https://github.com/example/whisper.cpp" },
		});
		expect((screen.getByRole("button", { name: copy.build }) as HTMLButtonElement).disabled).toBe(true);

		fireEvent.click(screen.getByLabelText(copy.riskAcknowledgement));
		await waitFor(() => expect((screen.getByRole("button", { name: copy.build }) as HTMLButtonElement).disabled).toBe(false));
	});
});
