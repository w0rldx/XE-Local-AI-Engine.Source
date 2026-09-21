// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { LlamaCppRuntimeStatus } from "@/features/node-settings/models/LocalRuntimeModels";
import type {
	LlamaCppSourceBuildPrerequisites,
	LlamaCppSourceBuildStatus,
} from "@/features/node-settings/models/SourceBuildModels";

const { state } = vi.hoisted(() => ({
	state: {
		prerequisites: { backend: "cpu", canBuild: true, items: [] } as LlamaCppSourceBuildPrerequisites,
		status: {
			phase: "Idle",
			isRunning: false,
			terminal: false,
			logStartSequence: 0,
			logLines: [],
			sanitizedError: null,
			currentBuild: null,
		} as LlamaCppSourceBuildStatus,
		runtime: {
			installed: null,
			recommendedTag: "b1",
			upstreamLatestTag: null,
			updateAvailable: false,
			isOffline: false,
			runningProcessCount: 0,
		} as LlamaCppRuntimeStatus,
		start: vi.fn(),
		cancel: vi.fn(),
		remove: vi.fn(),
		prerequisiteArgs: vi.fn(),
		statusArgs: vi.fn(),
	},
}));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, vars?: Record<string, unknown>) => {
			const labels: Record<string, string> = {
				"components.sourceBuild.buildFromSource": "Build from source",
				"pages.nodeSettings.llamaCpp.sourceBuild.title": "llama.cpp build from source",
				"pages.nodeSettings.llamaCpp.sourceBuild.description": "Description",
				"pages.nodeSettings.llamaCpp.sourceBuild.backend": "Backend",
				"pages.nodeSettings.llamaCpp.sourceBuild.backends.cpu": "CPU",
				"pages.nodeSettings.llamaCpp.sourceBuild.backends.vulkan": "Vulkan",
				"pages.nodeSettings.llamaCpp.sourceBuild.backends.cuda": "CUDA",
				"pages.nodeSettings.llamaCpp.sourceBuild.source": "Source",
				"pages.nodeSettings.llamaCpp.sourceBuild.sources.official": "Official upstream",
				"pages.nodeSettings.llamaCpp.sourceBuild.sources.custom": "Custom public fork",
				"pages.nodeSettings.llamaCpp.sourceBuild.revisions.enginePinned": "Engine-pinned revision",
				"pages.nodeSettings.llamaCpp.sourceBuild.prerequisites.os-is-linux": "Linux host",
				"pages.nodeSettings.llamaCpp.sourceBuild.prerequisiteAvailability.available": "Verfügbar",
				"pages.nodeSettings.llamaCpp.sourceBuild.repository": "GitHub repository",
				"pages.nodeSettings.llamaCpp.sourceBuild.riskWarning": "Trusted code warning",
				"pages.nodeSettings.llamaCpp.sourceBuild.riskAcknowledgement": "I accept the code-execution risk",
				"pages.nodeSettings.llamaCpp.sourceBuild.commit": "Commit SHA (optional)",
				"pages.nodeSettings.llamaCpp.sourceBuild.build": "Build",
				"pages.nodeSettings.llamaCpp.sourceBuild.rebuild": "Rebuild",
				"pages.nodeSettings.llamaCpp.sourceBuild.cancel": "Cancel",
				"pages.nodeSettings.llamaCpp.sourceBuild.remove": "Remove",
				"pages.nodeSettings.llamaCpp.sourceBuild.active": `Active ${String(vars?.["backend"] ?? "")} source runtime`,
			};
			return labels[key] ?? key;
		},
	}),
}));

// Pinned OFF for the whole file. The card was ungated on 2026-09-19 and must not consult this store again: every
// assertion below therefore describes a node whose operator has never touched Developer Mode.
vi.mock("@/core/dev-tools/stores/DeveloperModeStore", () => ({
	useDeveloperModeStore: (selector: (value: { developerMode: boolean }) => unknown) => selector({ developerMode: false }),
}));

vi.mock("@/core/ui/notifications/Toast", () => ({ toast: { error: vi.fn() } }));

vi.mock("@/features/node-settings/queries/useLocalRuntime", () => ({
	useSourceBuildPrerequisites: (backend: string, enabled: boolean) => {
		state.prerequisiteArgs(backend, enabled);
		return { data: state.prerequisites };
	},
	useSourceBuildStatus: (enabled: boolean) => {
		state.statusArgs(enabled);
		return { data: state.status };
	},
	useLlamaCppRuntimeStatus: () => ({ data: state.runtime }),
	useStartSourceBuild: () => ({ mutate: state.start, isPending: false }),
	useCancelSourceBuild: () => ({ mutate: state.cancel, isPending: false }),
	useRemoveSourceBuild: () => ({ mutate: state.remove, isPending: false }),
}));

vi.mock("@/features/node-settings/hooks/useSourceBuildHub", () => ({
	useSourceBuildHub: () => ({ phase: null, logEntries: [], error: null, buildIdentity: null, reset: vi.fn() }),
}));

import { ApiError } from "@/core/api/errors/ApiError";
import { toast } from "@/core/ui/notifications/Toast";
import { SourceBuildCard } from "@/features/node-settings/components/SourceBuildCard";
import { testMantineTheme } from "@/test/MantineTestRender";

function renderCard(): void {
	render(
		<MantineProvider env="test" theme={testMantineTheme}>
			<SourceBuildCard />
		</MantineProvider>,
	);
}

/**
 * Opens the build form. Everything that measures the toolchain lives behind this disclosure, because the probe behind
 * it spawns a compiler-toolchain's worth of child processes and all three source-build cards render unconditionally.
 */
async function openBuildForm(): Promise<void> {
	fireEvent.click(screen.getByTestId("source-build-form-toggle"));
	// `keepMounted={false}` means the controls are MOUNTED by the open, not merely revealed, so the first one has to
	// be awaited rather than queried synchronously.
	await screen.findByRole("combobox", { name: "Backend" });
}

describe("SourceBuildCard", () => {
	beforeEach(() => {
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn(() => ({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() })),
		});
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class {
				observe() {
					// Intentionally empty test double.
				}
				unobserve() {
					// Intentionally empty test double.
				}
				disconnect() {
					// Intentionally empty test double.
				}
			},
		});
		state.runtime = {
			installed: null,
			recommendedTag: "b1",
			upstreamLatestTag: null,
			updateAvailable: false,
			isOffline: false,
			runningProcessCount: 0,
		};
		state.status = {
			phase: "Idle",
			isRunning: false,
			terminal: false,
			logStartSequence: 0,
			logLines: [],
			sanitizedError: null,
			currentBuild: null,
		};
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("surfaces the blocked-build reason from a 409 instead of an empty notification", async () => {
		// Regression: the 409 body is `{ reason, message }` (not ProblemDetails), so the interceptor's ApiError used to
		// carry an undefined message and `toast.error(undefined)` rendered a blank notification.
		state.prerequisites = { backend: "cpu", canBuild: true, items: [] };
		state.start.mockImplementation((_draft: unknown, options?: { onError?: (error: unknown) => void }) => {
			options?.onError?.(
				new ApiError(409, {
					reason: "processes-running",
					message: "Stop or eject all running llama.cpp models before building the runtime.",
				} as never),
			);
		});
		renderCard();
		await openBuildForm();

		fireEvent.click(screen.getByRole("button", { name: "Build" }));

		await waitFor(() =>
			expect(toast.error).toHaveBeenCalledWith("Stop or eject all running llama.cpp models before building the runtime."),
		);
	});

	// The ungating, stated as behaviour: the card renders in full and subscribes to the node on a machine where
	// Developer Mode is off, and it passes no `enabled` flag at all — the queries are unconditional now.
	// The reason the disclosure exists: each prerequisite GET really RUNS the toolchain (`cmake --version`, `gcc`,
	// `g++`, `ninja`/`make`, `git`, plus the backend's own tools), and three of these cards render on Node Settings
	// for every operator. `enabled` is the proof here — a disabled query cannot be in flight, which is the hole a bare
	// "no request yet" assertion right after render would leave open.
	it("asks for no toolchain probe while the build form is closed", async () => {
		renderCard();

		expect(state.prerequisiteArgs).toHaveBeenCalledWith("cpu", false);
		expect(state.prerequisiteArgs.mock.calls.every((call: readonly unknown[]) => call[1] === false)).toBe(true);
		// `keepMounted={false}`: a closed form holds no focusable inputs either.
		expect(screen.queryByRole("combobox", { name: "Backend" })).toBeNull();
		expect(screen.queryByRole("button", { name: "Build" })).toBeNull();

		await openBuildForm();

		expect(state.prerequisiteArgs).toHaveBeenLastCalledWith("cpu", true);
	});

	it("names the collapsed form for a screen reader and points it at the region it controls", async () => {
		renderCard();

		const toggle = screen.getByRole("button", { name: "Build from source" });
		expect(toggle.getAttribute("aria-expanded")).toBe("false");
		// The region has to exist while collapsed, or `aria-controls` points at nothing in the one state it describes.
		const controlled = toggle.getAttribute("aria-controls");
		expect(controlled).toBeTruthy();
		expect(document.getElementById(controlled as string)).not.toBeNull();

		await openBuildForm();
		expect(toggle.getAttribute("aria-expanded")).toBe("true");
	});

	// A running build and a failure each mean the form IS the next thing the operator needs, so it opens itself —
	// decided from the status read alone, never by probing.
	it("expands the build form on its own while a build is running", async () => {
		state.status = { ...state.status, phase: "Building", isRunning: true };
		renderCard();

		expect(await screen.findByRole("combobox", { name: "Backend" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Build from source" }).getAttribute("aria-expanded")).toBe("true");
		expect(state.prerequisiteArgs).toHaveBeenLastCalledWith("cpu", true);
	});

	it("expands the build form on its own after a build failed, because the form is the retry", async () => {
		state.status = { ...state.status, phase: "Failed", terminal: true, sanitizedError: "The build failed." };
		renderCard();

		expect(await screen.findByRole("combobox", { name: "Backend" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Build from source" }).getAttribute("aria-expanded")).toBe("true");
	});

	it("renders in full with developer mode off", async () => {
		state.prerequisites = { backend: "cpu", canBuild: true, items: [] };
		renderCard();
		await openBuildForm();

		expect(screen.getByTestId("source-build-card")).toBeTruthy();
		expect(screen.getByRole("button", { name: "Build" })).toBeTruthy();
		// The status read is unconditional — it costs nothing. The probe is not: it is asked for only once the form
		// that needs its answer is open, which is the whole point of the disclosure.
		expect(state.statusArgs).toHaveBeenCalledWith(undefined);
		expect(state.prerequisiteArgs).toHaveBeenNthCalledWith(1, "cpu", false);
		expect(state.prerequisiteArgs).toHaveBeenLastCalledWith("cpu", true);
	});

	it("hydrates explicit custom provenance even when it uses the canonical official repository URL", async () => {
		state.runtime = {
			...state.runtime,
			installed: {
				tag: "b1",
				variant: "vulkan",
				asset: "source",
				installedAtUtc: 1,
				isSourceBuild: true,
				sourceRepository: "https://github.com/ggml-org/llama.cpp",
				sourceCommit: "a".repeat(40),
				sourceSelection: "custom",
				sourceRevisionMode: "explicitCommit",
				sourceRequestedCommit: "b".repeat(40),
			},
		};
		renderCard();
		await openBuildForm();

		await waitFor(() =>
			expect((screen.getByLabelText("GitHub repository") as HTMLInputElement).value).toBe(
				"https://github.com/ggml-org/llama.cpp",
			),
		);
		const acknowledgement = screen.getByLabelText("I accept the code-execution risk") as HTMLInputElement;
		expect(acknowledgement.checked).toBe(false);
		expect((screen.getByRole("button", { name: "Rebuild" }) as HTMLButtonElement).disabled).toBe(true);

		fireEvent.click(acknowledgement);
		fireEvent.click(screen.getByRole("button", { name: "Rebuild" }));
		expect(state.start).toHaveBeenCalledWith(
			expect.objectContaining({
				backend: "vulkan",
				source: "custom",
				repository: "https://github.com/ggml-org/llama.cpp",
				commit: "b".repeat(40),
				acknowledgeCustomSourceRisk: true,
			}),
			expect.any(Object),
		);
		expect(acknowledgement.checked).toBe(false);
	});

	it("submits an optional explicit commit for official upstream", async () => {
		state.prerequisites = { backend: "cpu", canBuild: true, items: [] };
		renderCard();
		await openBuildForm();

		fireEvent.change(screen.getByLabelText("Commit SHA (optional)"), { target: { value: "A".repeat(40) } });
		fireEvent.click(screen.getByRole("button", { name: "Build" }));

		expect(state.start).toHaveBeenCalledWith(
			expect.objectContaining({ source: "official", commit: "A".repeat(40) }),
			expect.any(Object),
		);
	});

	it("blocks an official build on a malformed commit", async () => {
		state.prerequisites = { backend: "cpu", canBuild: true, items: [] };
		renderCard();
		await openBuildForm();

		fireEvent.change(screen.getByLabelText("Commit SHA (optional)"), { target: { value: "abc123" } });

		expect((screen.getByRole("button", { name: "Build" }) as HTMLButtonElement).disabled).toBe(true);
	});

	it("renders active provenance exclusively from the installed runtime", async () => {
		state.runtime = {
			...state.runtime,
			installed: {
				tag: "b1",
				variant: "cpu",
				asset: "source",
				installedAtUtc: 1,
				isSourceBuild: true,
				sourceRepository: "https://github.com/ggml-org/llama.cpp",
				sourceCommit: "c".repeat(40),
				sourceSelection: "official",
				sourceRevisionMode: "enginePinned",
				sourceRequestedCommit: null,
			},
		};
		state.status = {
			...state.status,
			currentBuild: {
				buildId: "11111111-1111-4111-8111-111111111111",
				backend: "vulkan",
				source: "custom",
				repository: "https://github.com/example/fork",
				revisionMode: "defaultBranch",
				requestedCommit: null,
				resolvedCommit: "d".repeat(40),
			},
		};
		state.prerequisites = {
			backend: "cpu",
			canBuild: true,
			items: [{ key: "os-is-linux", satisfied: true, detail: "Linux host detected." }],
		};
		renderCard();
		await openBuildForm();
		const provenance = screen.getByText(/github.com\/ggml-org\/llama.cpp/);
		expect(screen.queryByText(/github.com\/example\/fork/)).toBeNull();
		expect(screen.getByText(/Engine-pinned revision/)).toBeTruthy();
		expect(provenance.textContent).toContain("Official upstream");
		expect(screen.getByText("Linux host")).toBeTruthy();
		expect(screen.getByText("Verfügbar")).toBeTruthy();
		expect(screen.queryByText("Linux host detected.")).toBeNull();
	});
});
