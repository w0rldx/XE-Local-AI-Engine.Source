// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { LlamaCppRuntimeStatus } from "@/features/node-settings/models/LocalRuntimeModels";

// Hoisted mock handles for the data-layer hooks and the developer-mode flag, so each test can drive the panel state.
const { hooksMock, devModeMock } = vi.hoisted(() => ({
	hooksMock: {
		statusData: undefined as LlamaCppRuntimeStatus | undefined,
		isFetching: false,
		isLoading: false,
		refetch: vi.fn(() => Promise.resolve()),
		mutate: vi.fn(),
		isPending: false,
	},
	devModeMock: { developerMode: false },
}));

vi.mock("@/features/node-settings/queries/useLocalRuntime", () => ({
	useLlamaCppRuntimeStatus: () => ({
		data: hooksMock.statusData,
		isFetching: hooksMock.isFetching,
		isLoading: hooksMock.isLoading,
		error: null,
		refetch: hooksMock.refetch,
	}),
	useUpdateLlamaCppRuntime: () => ({ mutate: hooksMock.mutate, isPending: hooksMock.isPending }),
}));

vi.mock("@/core/dev-tools/stores/DeveloperModeStore", () => ({
	useDeveloperModeStore: (selector: (state: { developerMode: boolean }) => unknown) =>
		selector({ developerMode: devModeMock.developerMode }),
}));

vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { progress: vi.fn(), success: vi.fn(), error: vi.fn() },
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
		devModeMock.developerMode = false;
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("shows installed vs recommended and an enabled update button when an update is available", () => {
		hooksMock.statusData = {
			installed: { tag: "b1000", variant: "cpu", asset: "a", installedAtUtc: 0 },
			recommendedTag: "b9692",
			upstreamLatestTag: null,
			updateAvailable: true,
			isOffline: false,
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
		};

		renderPanel();

		expect(screen.getByTestId("llamacpp-updater-state-uptodate")).toBeTruthy();
		expect((screen.getByTestId("llamacpp-updater-update-button") as HTMLButtonElement).disabled).toBe(true);
	});

	it("hides the upstream-latest tag unless developer mode is on", () => {
		hooksMock.statusData = {
			installed: { tag: "b1000", variant: "cpu", asset: "a", installedAtUtc: 0 },
			recommendedTag: "b9692",
			upstreamLatestTag: "b9999",
			updateAvailable: true,
			isOffline: false,
		};

		renderPanel();
		expect(screen.queryByTestId("llamacpp-updater-upstream")).toBeNull();

		cleanup();
		devModeMock.developerMode = true;
		renderPanel();
		expect(screen.getByTestId("llamacpp-updater-upstream").textContent).toContain("b9999");
		expect(screen.getByTestId("llamacpp-updater-upstream-button")).toBeTruthy();
	});

	it("disables the update button and shows the offline notice when offline", () => {
		hooksMock.statusData = {
			installed: { tag: "b1000", variant: "cpu", asset: "a", installedAtUtc: 0 },
			recommendedTag: "b9692",
			upstreamLatestTag: null,
			updateAvailable: true,
			isOffline: true,
		};

		renderPanel();

		expect(screen.getByTestId("llamacpp-updater-offline")).toBeTruthy();
		expect((screen.getByTestId("llamacpp-updater-update-button") as HTMLButtonElement).disabled).toBe(true);
	});
});
