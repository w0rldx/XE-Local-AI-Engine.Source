// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type {
	ImageRuntimeSourceBuildPrerequisites,
	ImageRuntimeSourceBuildStatus,
	ImageRuntimeStatus,
} from "@/features/node-settings/models/ImageRuntimeSourceBuildModels";

const { state, devMode } = vi.hoisted(() => ({
	state: {
		prerequisites: { backend: "cpu", canBuild: true, items: [] } as ImageRuntimeSourceBuildPrerequisites,
		status: {
			phase: "idle",
			isRunning: false,
			terminal: false,
			logStartSequence: 0,
			logLines: [],
			sanitizedError: null,
			currentBuild: null,
		} as ImageRuntimeSourceBuildStatus,
		runtime: {
			managedRuntime: null,
			activity: {
				activeJobCount: 0,
				spawnReadinessCount: 0,
				residentProcessCount: 0,
				mutationReserved: false,
				evictionReserved: false,
				isBusy: false,
			},
		} as ImageRuntimeStatus,
		start: vi.fn(),
		cancel: vi.fn(),
		remove: vi.fn(),
		eject: vi.fn(),
		prerequisiteArgs: vi.fn(),
		statusArgs: vi.fn(),
		runtimeArgs: vi.fn(),
	},
	devMode: { enabled: true },
}));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, vars?: Record<string, unknown>) => {
			const labels: Record<string, string> = {
				"pages.nodeSettings.imageRuntime.sourceBuild.title": "stable-diffusion.cpp build from source",
				"pages.nodeSettings.imageRuntime.sourceBuild.devBadge": "Dev",
				"pages.nodeSettings.imageRuntime.sourceBuild.recoveryBadge": "Recovery",
				"pages.nodeSettings.imageRuntime.sourceBuild.recoveryDescription": "Invalid runtime recovery",
				"pages.nodeSettings.imageRuntime.sourceBuild.description": "Description",
				"pages.nodeSettings.imageRuntime.sourceBuild.backend": "Backend",
				"pages.nodeSettings.imageRuntime.sourceBuild.backends.cpu": "CPU",
				"pages.nodeSettings.imageRuntime.sourceBuild.backends.vulkan": "Vulkan",
				"pages.nodeSettings.imageRuntime.sourceBuild.backends.cuda": "CUDA",
				"pages.nodeSettings.imageRuntime.sourceBuild.source": "Source",
				"pages.nodeSettings.imageRuntime.sourceBuild.sources.official": "Official upstream",
				"pages.nodeSettings.imageRuntime.sourceBuild.sources.custom": "Custom public fork",
				"pages.nodeSettings.imageRuntime.sourceBuild.revisions.explicitCommit": "Explicit commit",
				"pages.nodeSettings.imageRuntime.sourceBuild.revisionBehavior.enginePinned": "Pinned by the engine",
				"pages.nodeSettings.imageRuntime.sourceBuild.revisionBehavior.explicitCommit": "Exact commit selected",
				"pages.nodeSettings.imageRuntime.sourceBuild.repository": "GitHub repository",
				"pages.nodeSettings.imageRuntime.sourceBuild.commit": "Commit SHA (optional)",
				"pages.nodeSettings.imageRuntime.sourceBuild.riskWarning": "Trusted code warning",
				"pages.nodeSettings.imageRuntime.sourceBuild.riskAcknowledgement": "I accept the code-execution risk",
				"pages.nodeSettings.imageRuntime.sourceBuild.activity.idle": "Image runtime idle",
				"pages.nodeSettings.imageRuntime.sourceBuild.activity.busy": "Image runtime busy",
				"pages.nodeSettings.imageRuntime.sourceBuild.activity.detail": `${String(vars?.["jobs"] ?? "")} jobs, ${String(vars?.["processes"] ?? "")} processes`,
				"pages.nodeSettings.imageRuntime.sourceBuild.validity.invalid": `Invalid ${String(vars?.["backend"] ?? "")} runtime`,
				"pages.nodeSettings.imageRuntime.sourceBuild.build": "Build",
				"pages.nodeSettings.imageRuntime.sourceBuild.rebuild": "Rebuild",
				"pages.nodeSettings.imageRuntime.sourceBuild.cancel": "Cancel",
				"pages.nodeSettings.imageRuntime.sourceBuild.eject": "Eject image processes",
				"pages.nodeSettings.imageRuntime.sourceBuild.remove": "Remove",
			};
			return labels[key] ?? key;
		},
	}),
}));

vi.mock("@/core/dev-tools/stores/DeveloperModeStore", () => ({
	useDeveloperModeStore: (selector: (value: { developerMode: boolean }) => unknown) =>
		selector({ developerMode: devMode.enabled }),
}));

vi.mock("@/core/ui/notifications/Toast", () => ({ toast: { error: vi.fn() } }));

vi.mock("@/features/node-settings/queries/useImageRuntime", () => ({
	useImageRuntimeSourceBuildPrerequisites: (backend: string, enabled: boolean) => {
		state.prerequisiteArgs(backend, enabled);
		return { data: state.prerequisites };
	},
	useImageRuntimeSourceBuildStatus: (enabled: boolean) => {
		state.statusArgs(enabled);
		return { data: state.status };
	},
	useImageRuntimeStatus: (enabled: boolean) => {
		state.runtimeArgs(enabled);
		return { data: state.runtime };
	},
	useStartImageRuntimeSourceBuild: () => ({ mutate: state.start, isPending: false }),
	useCancelImageRuntimeSourceBuild: () => ({ mutate: state.cancel, isPending: false }),
	useRemoveImageRuntimeSourceBuild: () => ({ mutate: state.remove, isPending: false }),
	useEjectImageRuntime: () => ({ mutate: state.eject, isPending: false }),
}));

vi.mock("@/features/node-settings/hooks/useImageRuntimeSourceBuildHub", () => ({
	useImageRuntimeSourceBuildHub: () => ({
		phase: null,
		logEntries: [],
		error: null,
		buildIdentity: null,
		reset: vi.fn(),
	}),
}));

import { ImageRuntimeSourceBuildCard } from "@/features/node-settings/components/ImageRuntimeSourceBuildCard";

function renderCard(): void {
	render(
		<MantineProvider>
			<ImageRuntimeSourceBuildCard />
		</MantineProvider>,
	);
}

describe("ImageRuntimeSourceBuildCard", () => {
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
		Element.prototype.scrollIntoView = vi.fn();
		devMode.enabled = true;
		state.prerequisites = { backend: "cpu", canBuild: true, items: [] };
		state.status = {
			phase: "idle",
			isRunning: false,
			terminal: false,
			logStartSequence: 0,
			logLines: [],
			sanitizedError: null,
			currentBuild: null,
		};
		state.runtime = {
			managedRuntime: null,
			activity: {
				activeJobCount: 0,
				spawnReadinessCount: 0,
				residentProcessCount: 0,
				mutationReserved: false,
				evictionReserved: false,
				isBusy: false,
			},
		};
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("hides the card and disables every server subscription outside developer mode", () => {
		devMode.enabled = false;
		renderCard();

		expect(screen.queryByTestId("image-runtime-source-build-card")).toBeNull();
		expect(state.prerequisiteArgs).toHaveBeenCalledWith("cpu", false);
		expect(state.statusArgs).toHaveBeenCalledWith(false);
		expect(state.runtimeArgs).toHaveBeenCalledWith(true);
	});

	it("exposes invalid-runtime removal outside developer mode without exposing build controls", () => {
		devMode.enabled = false;
		state.runtime = {
			managedRuntime: {
				validity: "invalid",
				desiredBackend: "cuda",
				sourceRepository: "https://github.com/leejet/stable-diffusion.cpp",
				sourceCommit: "a".repeat(40),
				sourceSelection: "official",
				sourceRevisionMode: "enginePinned",
				sourceRequestedCommit: null,
				installedAtUtc: 1,
				invalidReason: "The managed binary failed integrity verification.",
			},
			activity: {
				activeJobCount: 0,
				spawnReadinessCount: 0,
				residentProcessCount: 0,
				mutationReserved: false,
				evictionReserved: false,
				isBusy: false,
			},
		};
		renderCard();

		expect(screen.getByText("Recovery")).toBeTruthy();
		expect(screen.getByText("Invalid runtime recovery")).toBeTruthy();
		expect(screen.queryByRole("button", { name: "Rebuild" })).toBeNull();
		const remove = screen.getByRole("button", { name: "Remove" }) as HTMLButtonElement;
		expect(remove.disabled).toBe(false);
		fireEvent.click(remove);
		expect(state.remove).toHaveBeenCalledWith(undefined, expect.any(Object));
		expect(state.prerequisiteArgs).toHaveBeenCalledWith("cpu", false);
		expect(state.statusArgs).toHaveBeenCalledWith(false);
		expect(state.runtimeArgs).toHaveBeenCalledWith(true);
	});

	it("exposes invalid-runtime ejection outside developer mode when only a resident process remains", () => {
		devMode.enabled = false;
		state.runtime = {
			managedRuntime: {
				validity: "invalid",
				desiredBackend: "cuda",
				sourceRepository: "https://github.com/leejet/stable-diffusion.cpp",
				sourceCommit: "a".repeat(40),
				sourceSelection: "official",
				sourceRevisionMode: "enginePinned",
				sourceRequestedCommit: null,
				installedAtUtc: 1,
				invalidReason: "The managed binary failed integrity verification.",
			},
			activity: {
				activeJobCount: 0,
				spawnReadinessCount: 0,
				residentProcessCount: 1,
				mutationReserved: false,
				evictionReserved: false,
				isBusy: true,
			},
		};
		renderCard();

		const eject = screen.getByRole("button", { name: "Eject image processes" }) as HTMLButtonElement;
		expect(eject.disabled).toBe(false);
		fireEvent.click(eject);
		expect(state.eject).toHaveBeenCalledWith(undefined, expect.any(Object));
		expect((screen.getByRole("button", { name: "Remove" }) as HTMLButtonElement).disabled).toBe(true);
	});

	it("explains the pinned official revision and starts a CPU build", () => {
		renderCard();

		expect(screen.getByTestId("image-runtime-revision-behavior").textContent).toBe("Pinned by the engine");
		fireEvent.click(screen.getByRole("button", { name: "Build" }));

		expect(state.start).toHaveBeenCalledWith(
			{
				backend: "cpu",
				source: "official",
				repository: "",
				commit: "",
				acknowledgeCustomSourceRisk: false,
			},
			expect.any(Object),
		);
	});

	it("requires trust for a custom explicit commit and clears acknowledgement after start", async () => {
		renderCard();

		fireEvent.click(screen.getByRole("combobox", { name: "Source" }));
		fireEvent.click(await screen.findByRole("option", { name: "Custom public fork" }));
		fireEvent.change(screen.getByLabelText("GitHub repository"), {
			target: { value: "https://github.com/example/stable-diffusion.cpp" },
		});
		fireEvent.change(screen.getByLabelText("Commit SHA (optional)"), {
			target: { value: "A".repeat(40) },
		});
		expect(screen.getByTestId("image-runtime-revision-behavior").textContent).toBe("Exact commit selected");
		const acknowledgement = screen.getByLabelText("I accept the code-execution risk") as HTMLInputElement;
		expect((screen.getByRole("button", { name: "Build" }) as HTMLButtonElement).disabled).toBe(true);

		fireEvent.click(acknowledgement);
		fireEvent.click(screen.getByRole("button", { name: "Build" }));

		expect(state.start).toHaveBeenCalledWith(
			expect.objectContaining({
				source: "custom",
				repository: "https://github.com/example/stable-diffusion.cpp",
				commit: "A".repeat(40),
				acknowledgeCustomSourceRisk: true,
			}),
			expect.any(Object),
		);
		await waitFor(() => expect(acknowledgement.checked).toBe(false));
	});

	it("recovers persisted build logs and exposes cancellation while a build is running", () => {
		state.status = {
			phase: "building",
			isRunning: true,
			terminal: false,
			logStartSequence: 12,
			logLines: ["Configuring CUDA", "Compiling stable-diffusion.cpp"],
			sanitizedError: null,
			currentBuild: {
				buildId: "11111111-1111-4111-8111-111111111111",
				backend: "cuda",
				source: "official",
				repository: "https://github.com/leejet/stable-diffusion.cpp",
				revisionMode: "enginePinned",
				requestedCommit: null,
				resolvedCommit: null,
			},
		};
		renderCard();

		expect(screen.getByTestId("cuda-build-log-content").textContent).toContain("Compiling stable-diffusion.cpp");
		fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
		expect(state.cancel).toHaveBeenCalledWith(undefined, expect.any(Object));
	});

	it("shows invalid managed provenance and permits eject only when resident processes are otherwise idle", () => {
		state.runtime = {
			managedRuntime: {
				validity: "invalid",
				desiredBackend: "cuda",
				sourceRepository: "https://github.com/example/stable-diffusion.cpp",
				sourceCommit: "a".repeat(40),
				sourceSelection: "custom",
				sourceRevisionMode: "explicitCommit",
				sourceRequestedCommit: "b".repeat(40),
				installedAtUtc: 1,
				invalidReason: "The managed binary failed its smoke test.",
			},
			activity: {
				activeJobCount: 0,
				spawnReadinessCount: 0,
				residentProcessCount: 1,
				mutationReserved: false,
				evictionReserved: false,
				isBusy: true,
			},
		};
		renderCard();

		expect(screen.getByText("Invalid cuda runtime")).toBeTruthy();
		expect(screen.getByText("The managed binary failed its smoke test.")).toBeTruthy();
		expect((screen.getByRole("button", { name: "Rebuild" }) as HTMLButtonElement).disabled).toBe(true);
		expect((screen.getByRole("button", { name: "Remove" }) as HTMLButtonElement).disabled).toBe(true);

		const eject = screen.getByRole("button", { name: "Eject image processes" }) as HTMLButtonElement;
		expect(eject.disabled).toBe(false);
		fireEvent.click(eject);
		expect(state.eject).toHaveBeenCalledWith(undefined, expect.any(Object));
	});
});
