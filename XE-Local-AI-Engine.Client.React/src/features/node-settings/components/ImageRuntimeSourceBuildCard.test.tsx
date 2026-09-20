// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type {
	ImageRuntimeSourceBuildPrerequisites,
	ImageRuntimeSourceBuildStatus,
	ImageRuntimeStatus,
} from "@/features/node-settings/models/ImageRuntimeSourceBuildModels";

const { state } = vi.hoisted(() => ({
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
}));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, vars?: Record<string, unknown>) => {
			const labels: Record<string, string> = {
				"components.sourceBuild.buildFromSource": "Build from source",
				"pages.nodeSettings.imageRuntime.sourceBuild.title": "stable-diffusion.cpp build from source",
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

// Pinned OFF for the whole file. The card was ungated on 2026-09-19 and must not consult this store again: every
// assertion below therefore describes a node whose operator has never touched Developer Mode.
vi.mock("@/core/dev-tools/stores/DeveloperModeStore", () => ({
	useDeveloperModeStore: (selector: (value: { developerMode: boolean }) => unknown) => selector({ developerMode: false }),
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
		<MantineProvider env="test">
			<ImageRuntimeSourceBuildCard />
		</MantineProvider>,
	);
}

/**
 * Opens the build form. Everything that measures the toolchain lives behind this disclosure, because the probe behind
 * it spawns a compiler-toolchain's worth of child processes and all three source-build cards render unconditionally.
 */
async function openBuildForm(): Promise<void> {
	fireEvent.click(screen.getByTestId("image-runtime-source-build-form-toggle"));
	// `keepMounted={false}` means the controls are MOUNTED by the open, not merely revealed, so the first one has to
	// be awaited rather than queried synchronously.
	await screen.findByRole("combobox", { name: "Backend" });
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

	// A running build, a failure and an invalid record each mean the form IS the next thing the operator needs, so it
	// opens itself — decided from the status reads alone, never by probing.
	it("expands the build form on its own while a build is running", async () => {
		state.status = { ...state.status, phase: "building", isRunning: true };
		renderCard();

		expect(await screen.findByRole("combobox", { name: "Backend" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Build from source" }).getAttribute("aria-expanded")).toBe("true");
		expect(state.prerequisiteArgs).toHaveBeenLastCalledWith("cpu", true);
	});

	it("expands the build form on its own after a build failed, because the form is the retry", async () => {
		state.status = { ...state.status, phase: "failed", terminal: true, sanitizedError: "The build failed." };
		renderCard();

		expect(await screen.findByRole("combobox", { name: "Backend" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Build from source" }).getAttribute("aria-expanded")).toBe("true");
	});

	it("expands the build form on its own for an invalid managed record, which a rebuild is the fix for", async () => {
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

		expect(await screen.findByRole("button", { name: "Rebuild" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Build from source" }).getAttribute("aria-expanded")).toBe("true");
	});

	it("renders in full with developer mode off", async () => {
		renderCard();
		await openBuildForm();

		expect(screen.getByTestId("image-runtime-source-build-card")).toBeTruthy();
		expect(screen.getByRole("button", { name: "Build" })).toBeTruthy();
		// The two status reads are unconditional — they cost nothing. The probe is not: it is asked for only once the
		// form that needs its answer is open, which is the whole point of the disclosure.
		expect(state.statusArgs).toHaveBeenCalledWith(undefined);
		expect(state.runtimeArgs).toHaveBeenCalledWith(undefined);
		expect(state.prerequisiteArgs).toHaveBeenNthCalledWith(1, "cpu", false);
		expect(state.prerequisiteArgs).toHaveBeenLastCalledWith("cpu", true);
	});

	// The fail-closed tombstone's in-app exit. It no longer depends on a mode, but the record must still state why it
	// is invalid and leave Remove usable — that part was always about recovery, not about Developer Mode.
	it("exposes invalid-runtime removal with its reason", async () => {
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

		expect(screen.getByText("Invalid cuda runtime")).toBeTruthy();
		expect(screen.getByText("The managed binary failed integrity verification.")).toBeTruthy();
		const remove = screen.getByRole("button", { name: "Remove" }) as HTMLButtonElement;
		expect(remove.disabled).toBe(false);
		fireEvent.click(remove);
		expect(state.remove).toHaveBeenCalledWith(undefined, expect.any(Object));
		// A rebuild is now offered beside the removal — the record is invalid, not the toolchain.
		expect(screen.getByRole("button", { name: "Rebuild" })).toBeTruthy();
	});

	it("exposes invalid-runtime ejection when only a resident process remains", async () => {
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

	it("explains the pinned official revision and starts a CPU build", async () => {
		renderCard();
		await openBuildForm();

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
		await openBuildForm();

		fireEvent.click(screen.getByRole("combobox", { name: "Source" }));
		// `hidden: true`: jsdom has no layout engine, so Mantine's popover dropdown never loses its `display: none`
		// even once open — the same accommodation every other Select test in this repo makes.
		fireEvent.click(await screen.findByRole("option", { name: "Custom public fork", hidden: true }));
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

	it("recovers persisted build logs and exposes cancellation while a build is running", async () => {
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

	// Regression: the draft used to be re-seeded by an effect keyed on the `managed` object, so every refetch of the
	// runtime status — a fresh object each time — replaced whatever the operator had just typed.
	it("keeps an in-progress draft across a status refetch and re-seeds only when a new runtime is installed", async () => {
		const managed = {
			validity: "active",
			desiredBackend: "cpu",
			sourceRepository: "https://github.com/example/original.cpp",
			sourceCommit: "a".repeat(40),
			sourceSelection: "custom",
			sourceRevisionMode: "enginePinned",
			sourceRequestedCommit: null,
			installedAtUtc: 1,
			invalidReason: null,
		} as const;
		const idle = {
			activeJobCount: 0,
			spawnReadinessCount: 0,
			residentProcessCount: 0,
			mutationReserved: false,
			evictionReserved: false,
			isBusy: false,
		} as const;
		state.runtime = { managedRuntime: { ...managed }, activity: { ...idle } };
		// A fresh element each time: React bails out of re-rendering a referentially identical one, which would make
		// the surviving draft below prove nothing.
		const card = () => (
			<MantineProvider env="test">
				<ImageRuntimeSourceBuildCard />
			</MantineProvider>
		);
		const { rerender } = render(card());
		// A healthy adopted runtime leaves the form collapsed, and the draft this test is about lives inside it.
		await openBuildForm();

		fireEvent.change(screen.getByLabelText("GitHub repository"), {
			target: { value: "https://github.com/example/edited.cpp" },
		});

		// Same installed runtime, different payload: a status refetch, not a new install. The edit must survive.
		state.runtime = {
			managedRuntime: { ...managed, sourceRepository: "https://github.com/example/refetched.cpp" },
			activity: { ...idle },
		};
		rerender(card());
		expect((screen.getByLabelText("GitHub repository") as HTMLInputElement).value).toBe("https://github.com/example/edited.cpp");

		// A completed build installs a new runtime (a new installedAtUtc), which IS a new server state to seed from.
		state.runtime = {
			managedRuntime: { ...managed, sourceRepository: "https://github.com/example/rebuilt.cpp", installedAtUtc: 2 },
			activity: { ...idle },
		};
		rerender(card());
		expect((screen.getByLabelText("GitHub repository") as HTMLInputElement).value).toBe("https://github.com/example/rebuilt.cpp");
	});

	it("shows invalid managed provenance and permits eject only when resident processes are otherwise idle", async () => {
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
