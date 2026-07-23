// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { LlamaCppRuntimeStatus } from "@/features/node-settings/models/LocalRuntimeModels";
import type {
	LlamaCppSourceBuildPrerequisites,
	LlamaCppSourceBuildStatus,
} from "@/features/node-settings/models/SourceBuildModels";

const { state, devMode } = vi.hoisted(() => ({
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
	devMode: { enabled: true },
}));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, vars?: Record<string, unknown>) => {
			const labels: Record<string, string> = {
				"pages.nodeSettings.llamaCpp.sourceBuild.title": "llama.cpp build from source",
				"pages.nodeSettings.llamaCpp.sourceBuild.devBadge": "Dev",
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

vi.mock("@/core/dev-tools/stores/DeveloperModeStore", () => ({
	useDeveloperModeStore: (selector: (value: { developerMode: boolean }) => unknown) =>
		selector({ developerMode: devMode.enabled }),
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

function renderCard(): void {
	render(
		<MantineProvider>
			<SourceBuildCard />
		</MantineProvider>,
	);
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
		devMode.enabled = true;
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

		fireEvent.click(screen.getByRole("button", { name: "Build" }));

		await waitFor(() =>
			expect(toast.error).toHaveBeenCalledWith(
				"Stop or eject all running llama.cpp models before building the runtime.",
			),
		);
	});

	it("hides and disables queries when Developer mode is off", () => {
		devMode.enabled = false;
		renderCard();
		expect(screen.queryByTestId("source-build-card")).toBeNull();
		expect(state.prerequisiteArgs).toHaveBeenCalledWith("cpu", false);
		expect(state.statusArgs).toHaveBeenCalledWith(false);
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

	it("does not expose an explicit commit input for official upstream", () => {
		renderCard();

		expect(screen.queryByLabelText("Commit SHA (optional)")).toBeNull();
	});

	it("renders active provenance exclusively from the installed runtime", () => {
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
		const provenance = screen.getByText(/github.com\/ggml-org\/llama.cpp/);
		expect(screen.queryByText(/github.com\/example\/fork/)).toBeNull();
		expect(screen.getByText(/Engine-pinned revision/)).toBeTruthy();
		expect(provenance.textContent).toContain("Official upstream");
		expect(screen.getByText("Linux host")).toBeTruthy();
		expect(screen.getByText("Verfügbar")).toBeTruthy();
		expect(screen.queryByText("Linux host detected.")).toBeNull();
	});
});
