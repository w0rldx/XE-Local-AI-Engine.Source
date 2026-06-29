// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type {
	CudaBuildPrerequisites,
	CudaBuildStatus,
	LlamaCppRuntimeStatus,
} from "@/features/node-settings/models/LocalRuntimeModels";
import type { CudaBuildLiveState } from "@/features/node-settings/hooks/useCudaBuildHub";

// Deterministic i18n: t returns the supplied default (with {{count}} interpolation applied) so the human copy is
// asserted, not the raw key — this doubles as the i18n-keys-resolve check (the card never renders a bare dotted key).
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

// Hoisted mock handles for the data-layer hooks, the hub, and the developer-mode flag, so each test drives card state.
const { hooksMock, devModeMock } = vi.hoisted(() => ({
	hooksMock: {
		prereqData: undefined as CudaBuildPrerequisites | undefined,
		prereqIsLoading: false,
		runtimeData: undefined as LlamaCppRuntimeStatus | undefined,
		cudaStatusData: undefined as CudaBuildStatus | undefined,
		startPending: false,
		cancelPending: false,
		removePending: false,
		hub: {
			phase: null,
			logLines: [] as readonly string[],
			terminal: false,
			error: null,
			reset: vi.fn(),
		} as CudaBuildLiveState,
	},
	devModeMock: { developerMode: true },
}));

vi.mock("@/features/node-settings/queries/useLocalRuntime", () => ({
	useCudaBuildPrerequisites: () => ({
		data: hooksMock.prereqData,
		isLoading: hooksMock.prereqIsLoading,
		error: null,
	}),
	useLlamaCppRuntimeStatus: () => ({ data: hooksMock.runtimeData }),
	useCudaBuildStatus: () => ({ data: hooksMock.cudaStatusData }),
	useStartCudaBuild: () => ({ mutate: vi.fn(), isPending: hooksMock.startPending }),
	useCancelCudaBuild: () => ({ mutate: vi.fn(), isPending: hooksMock.cancelPending }),
	useRemoveCudaBuild: () => ({ mutate: vi.fn(), isPending: hooksMock.removePending }),
}));

vi.mock("@/features/node-settings/hooks/useCudaBuildHub", () => ({
	useCudaBuildHub: () => hooksMock.hub,
}));

vi.mock("@/core/dev-tools/stores/DeveloperModeStore", () => ({
	useDeveloperModeStore: (selector: (state: { developerMode: boolean }) => unknown) =>
		selector({ developerMode: devModeMock.developerMode }),
}));

vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { progress: vi.fn(), success: vi.fn(), error: vi.fn() },
}));

// The card renders a TanStack Router <Link> in the eject-first notice. Stub it with a plain anchor so the card mounts
// without a RouterProvider.
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

import { CudaBuildCard } from "@/features/node-settings/components/CudaBuildCard";

function renderCard(): void {
	render(
		<MantineProvider>
			<CudaBuildCard />
		</MantineProvider>,
	);
}

describe("CudaBuildCard", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		hooksMock.prereqData = undefined;
		hooksMock.prereqIsLoading = false;
		hooksMock.runtimeData = undefined;
		hooksMock.cudaStatusData = undefined;
		hooksMock.startPending = false;
		hooksMock.cancelPending = false;
		hooksMock.removePending = false;
		hooksMock.hub = { phase: null, logLines: [], terminal: false, error: null, reset: vi.fn() };
		devModeMock.developerMode = true;
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("renders nothing when developer mode is off", () => {
		devModeMock.developerMode = false;
		renderCard();
		expect(screen.queryByTestId("cuda-build-card")).toBeNull();
	});

	it("renders the prerequisite checklist with a ✓/✗ item per prerequisite", () => {
		hooksMock.prereqData = {
			items: [
				{ key: "os-is-linux", satisfied: true, detail: "Linux 6.18" },
				{ key: "nvcc", satisfied: false, detail: "not found on PATH" },
			],
			canBuild: false,
		};

		renderCard();

		expect(screen.getByTestId("cuda-build-checklist")).toBeTruthy();
		expect(screen.getByTestId("cuda-build-prereq-os-is-linux").textContent).toContain("Linux 6.18");
		expect(screen.getByTestId("cuda-build-prereq-nvcc").textContent).toContain("not found on PATH");
		// The static title resolves to its human copy (i18n keys resolve — no bare dotted key leaks into the DOM).
		expect(screen.getByText("CUDA (build from source)")).toBeTruthy();
	});

	it("disables the Build button when canBuild is false", () => {
		hooksMock.prereqData = {
			items: [{ key: "os-is-linux", satisfied: true, detail: "Linux 6.18" }],
			canBuild: false,
		};

		renderCard();

		expect((screen.getByTestId("cuda-build-start-button") as HTMLButtonElement).disabled).toBe(true);
	});

	it("enables the Build button when canBuild is true on a Linux host", () => {
		hooksMock.prereqData = {
			items: [
				{ key: "os-is-linux", satisfied: true, detail: "Linux 6.18" },
				{ key: "nvcc", satisfied: true, detail: "12.4" },
			],
			canBuild: true,
		};

		renderCard();

		expect((screen.getByTestId("cuda-build-start-button") as HTMLButtonElement).disabled).toBe(false);
	});

	it("shows a non-Linux notice and disables the Build button on a non-Linux host", () => {
		hooksMock.prereqData = {
			items: [{ key: "os-is-linux", satisfied: false, detail: "Windows" }],
			canBuild: false,
		};

		renderCard();

		expect(screen.getByTestId("cuda-build-not-linux")).toBeTruthy();
		expect((screen.getByTestId("cuda-build-start-button") as HTMLButtonElement).disabled).toBe(true);
	});

	it("renders the streamed build log and a Cancel button while a build is running", () => {
		hooksMock.prereqData = {
			items: [{ key: "os-is-linux", satisfied: true, detail: "Linux 6.18" }],
			canBuild: true,
		};
		hooksMock.cudaStatusData = {
			phase: "compiling",
			isRunning: true,
			terminal: false,
			logLines: ["[1/8] cmake configure", "[2/8] building ggml"],
			sanitizedError: null,
			tag: null,
		};

		renderCard();

		expect(screen.getByTestId("cuda-build-progress")).toBeTruthy();
		expect(screen.getByTestId("cuda-build-log-content").textContent).toContain("building ggml");
		expect(screen.getByTestId("cuda-build-cancel-button")).toBeTruthy();
	});

	it("shows the managed-CUDA-active state with Rebuild/Remove and a rebuild-available hint when stale", () => {
		hooksMock.prereqData = {
			items: [{ key: "os-is-linux", satisfied: true, detail: "Linux 6.18" }],
			canBuild: true,
		};
		hooksMock.runtimeData = {
			installed: { tag: "b9692", variant: "cuda", asset: "a", installedAtUtc: 0, isSourceBuild: true },
			recommendedTag: "b9692",
			upstreamLatestTag: null,
			updateAvailable: false,
			isOffline: false,
			runningProcessCount: 0,
			isSourceBuild: true,
			rebuildAvailable: true,
		};

		renderCard();

		expect(screen.getByTestId("cuda-build-active")).toBeTruthy();
		expect(screen.getByTestId("cuda-build-active-tag").textContent).toContain("b9692");
		expect(screen.getByTestId("cuda-build-rebuild-available")).toBeTruthy();
		expect(screen.getByTestId("cuda-build-rebuild-button")).toBeTruthy();
		expect(screen.getByTestId("cuda-build-remove-button")).toBeTruthy();
	});
});
