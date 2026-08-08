// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { LlamaCppRuntimeStatus } from "@/features/node-settings/models/LocalRuntimeModels";

// Deterministic i18n: t returns the supplied default (with {{tag}}/{{count}} interpolation skipped) so copy is readable
// in assertions. The card maps the variant badge through t(`…variants.vulkan`, "vulkan") — return the fallback so the
// human label ("Vulkan") is asserted, not the raw key.
vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, fallback?: string, vars?: Record<string, unknown>) => {
			const text = fallback ?? _key;
			if (vars === undefined) {
				return text;
			}
			return Object.entries(vars).reduce(
				(acc, [name, value]) => acc.replace(new RegExp(`{{${name}}}`, "g"), String(value)),
				text,
			);
		},
	}),
}));

// Hoisted mock handles for the data-layer hooks and the developer-mode flag, so each test can drive the panel state.
const { hooksMock, devModeMock } = vi.hoisted(() => ({
	hooksMock: {
		statusData: undefined as LlamaCppRuntimeStatus | undefined,
		isFetching: false,
		isLoading: false,
		refresh: vi.fn(() => Promise.resolve()),
		mutate: vi.fn(),
		ensureMutate: vi.fn(),
		isPending: false,
		sourceBuildRunning: false,
	},
	devModeMock: { developerMode: false },
}));

vi.mock("@/features/node-settings/queries/useLocalRuntime", () => ({
	useLlamaCppRuntimeStatus: () => ({
		data: hooksMock.statusData,
		isFetching: hooksMock.isFetching,
		isLoading: hooksMock.isLoading,
		error: null,
	}),
	useUpdateLlamaCppRuntime: () => ({ mutate: hooksMock.mutate, isPending: hooksMock.isPending }),
	useEnsureLlamaCppBinary: () => ({ mutate: hooksMock.ensureMutate, isPending: false }),
	useRefreshLlamaCppRuntime: () => hooksMock.refresh,
	useSourceBuildStatus: () => ({ data: { isRunning: hooksMock.sourceBuildRunning } }),
}));

vi.mock("@/core/dev-tools/stores/DeveloperModeStore", () => ({
	useDeveloperModeStore: (selector: (state: { developerMode: boolean }) => unknown) =>
		selector({ developerMode: devModeMock.developerMode }),
}));

vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { progress: vi.fn(), success: vi.fn(), error: vi.fn() },
}));

// The card renders a TanStack Router <Link> (inside Anchor component={Link}) in the eject-first notice. Stub the router
// Link with a plain anchor so the card mounts without a RouterProvider.
vi.mock("@tanstack/react-router", () => ({
	Link: ({ children, to, ...props }: { children: ReactNode; to: string; [key: string]: unknown }) => (
		<a href={to} {...props}>
			{children}
		</a>
	),
}));

function installJsdomEnvironmentMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
}

import { LlamaCppUpdaterPanel } from "@/features/node-settings/components/LlamaCppUpdaterPanel";

function renderPanel(): void {
	render(
		<MantineProvider>
			<LlamaCppUpdaterPanel />
		</MantineProvider>,
	);
}

describe("LlamaCppUpdaterPanel", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		hooksMock.statusData = undefined;
		hooksMock.isFetching = false;
		hooksMock.isLoading = false;
		hooksMock.isPending = false;
		hooksMock.sourceBuildRunning = false;
		devModeMock.developerMode = false;
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("shows the installed tag + variant on mount with no operator click", () => {
		hooksMock.statusData = {
			installed: { tag: "b9700", variant: "vulkan", asset: "a", installedAtUtc: 0 },
			recommendedTag: "b9700",
			upstreamLatestTag: null,
			updateAvailable: false,
			isOffline: false,
			runningProcessCount: 0,
		};

		renderPanel();

		expect(screen.getByTestId("llamacpp-updater-installed").textContent).toContain("b9700");
		// The variant badge maps through t(`…variants.vulkan`, "vulkan"); the deterministic i18n mock returns the fallback.
		expect(screen.getByTestId("llamacpp-updater-installed-variant").textContent).toContain("vulkan");
	});

	it("shows installed vs recommended and an enabled update button when an update is available", () => {
		hooksMock.statusData = {
			installed: { tag: "b1000", variant: "cpu", asset: "a", installedAtUtc: 0 },
			recommendedTag: "b9692",
			upstreamLatestTag: null,
			updateAvailable: true,
			isOffline: false,
			runningProcessCount: 0,
		};

		renderPanel();

		expect(screen.getByTestId("llamacpp-updater-installed").textContent).toContain("b1000");
		expect(screen.getByTestId("llamacpp-updater-recommended").textContent).toContain("b9692");
		expect(screen.getByTestId("llamacpp-updater-state-available")).toBeTruthy();
		expect((screen.getByTestId("llamacpp-updater-update-button") as HTMLButtonElement).disabled).toBe(false);
	});

	it("shows an up-to-date state and disables the update button when no update is available", () => {
		hooksMock.statusData = {
			installed: { tag: "b9692", variant: "cpu", asset: "a", installedAtUtc: 0 },
			recommendedTag: "b9692",
			upstreamLatestTag: null,
			updateAvailable: false,
			isOffline: false,
			runningProcessCount: 0,
		};

		renderPanel();

		expect(screen.getByTestId("llamacpp-updater-state-uptodate")).toBeTruthy();
		expect((screen.getByTestId("llamacpp-updater-update-button") as HTMLButtonElement).disabled).toBe(true);
	});

	it("hides the upstream-latest tag unless developer mode is on, then shows the dev upstream button", () => {
		hooksMock.statusData = {
			installed: { tag: "b1000", variant: "cpu", asset: "a", installedAtUtc: 0 },
			recommendedTag: "b9692",
			upstreamLatestTag: "b9999",
			updateAvailable: true,
			isOffline: false,
			runningProcessCount: 0,
		};

		renderPanel();
		expect(screen.queryByTestId("llamacpp-updater-upstream")).toBeNull();

		cleanup();
		devModeMock.developerMode = true;
		renderPanel();
		expect(screen.getByTestId("llamacpp-updater-upstream").textContent).toContain("b9999");
		expect(screen.getByTestId("llamacpp-updater-upstream-button")).toBeTruthy();
		expect((screen.getByTestId("llamacpp-updater-upstream-button") as HTMLButtonElement).disabled).toBe(false);
	});

	it("disables the update button and shows the offline notice when offline", () => {
		hooksMock.statusData = {
			installed: { tag: "b1000", variant: "cpu", asset: "a", installedAtUtc: 0 },
			recommendedTag: "b9692",
			upstreamLatestTag: null,
			updateAvailable: true,
			isOffline: true,
			runningProcessCount: 0,
		};

		renderPanel();

		expect(screen.getByTestId("llamacpp-updater-offline")).toBeTruthy();
		expect((screen.getByTestId("llamacpp-updater-update-button") as HTMLButtonElement).disabled).toBe(true);
	});

	it("disables both install buttons and shows the eject-first notice when llama.cpp processes are running", () => {
		devModeMock.developerMode = true;
		hooksMock.statusData = {
			installed: { tag: "b1000", variant: "cpu", asset: "a", installedAtUtc: 0 },
			recommendedTag: "b9692",
			upstreamLatestTag: "b9999",
			updateAvailable: true,
			isOffline: false,
			runningProcessCount: 2,
		};

		renderPanel();

		expect(screen.getByTestId("llamacpp-updater-running-notice").textContent).toContain("2");
		expect(screen.getByTestId("llamacpp-updater-loaded-models-link")).toBeTruthy();
		expect((screen.getByTestId("llamacpp-updater-update-button") as HTMLButtonElement).disabled).toBe(true);
		expect((screen.getByTestId("llamacpp-updater-upstream-button") as HTMLButtonElement).disabled).toBe(true);
		expect((screen.getByTestId("llamacpp-updater-ensure-button") as HTMLButtonElement).disabled).toBe(true);
	});

	it.each([
		["an installed source runtime", true, false],
		["an active source build", false, true],
	])("disables prebuilt install and ensure actions for %s", (_description, installedSource, sourceBuildRunning) => {
		devModeMock.developerMode = true;
		hooksMock.sourceBuildRunning = sourceBuildRunning;
		hooksMock.statusData = {
			installed: {
				tag: "b1000",
				variant: "cpu",
				asset: "a",
				installedAtUtc: 0,
				isSourceBuild: installedSource,
			},
			recommendedTag: "b9692",
			upstreamLatestTag: "b9999",
			updateAvailable: true,
			isOffline: false,
			runningProcessCount: 0,
		};

		renderPanel();

		expect(screen.getByTestId("llamacpp-updater-source-build-notice")).toBeTruthy();
		expect((screen.getByTestId("llamacpp-updater-update-button") as HTMLButtonElement).disabled).toBe(true);
		expect((screen.getByTestId("llamacpp-updater-upstream-button") as HTMLButtonElement).disabled).toBe(true);
		expect((screen.getByTestId("llamacpp-updater-ensure-button") as HTMLButtonElement).disabled).toBe(true);
	});

	it("ensures the installed variant by default (no silent cpu fall-back on a vulkan node)", () => {
		hooksMock.statusData = {
			installed: { tag: "b9700", variant: "vulkan", asset: "a", installedAtUtc: 0 },
			recommendedTag: "b9700",
			upstreamLatestTag: null,
			updateAvailable: false,
			isOffline: false,
			runningProcessCount: 0,
		};

		renderPanel();

		// Operator clicks "Ensure / select" WITHOUT touching the variant Select. The target must be the installed build
		// (vulkan), not the hard-coded cpu default — otherwise the GPU node silently re-ensures the CPU binary.
		fireEvent.click(screen.getByTestId("llamacpp-updater-ensure-button"));
		expect(hooksMock.ensureMutate.mock.calls[0]?.[0]).toBe("vulkan");
	});

	it("ensures cpu by default when nothing is installed yet", () => {
		hooksMock.statusData = {
			installed: null,
			recommendedTag: "b9700",
			upstreamLatestTag: null,
			updateAvailable: true,
			isOffline: false,
			runningProcessCount: 0,
		};

		renderPanel();

		fireEvent.click(screen.getByTestId("llamacpp-updater-ensure-button"));
		expect(hooksMock.ensureMutate.mock.calls[0]?.[0]).toBe("cpu");
	});
});
