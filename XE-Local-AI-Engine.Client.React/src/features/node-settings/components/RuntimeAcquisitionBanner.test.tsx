// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { RuntimeAcquisitionStatus } from "@/features/node-settings/queries/useLocalRuntime";

const { hooksMock } = vi.hoisted(() => ({
	hooksMock: {
		status: undefined as RuntimeAcquisitionStatus | undefined,
		enabledArg: undefined as boolean | undefined,
		ensureMutate: vi.fn(),
	},
}));

vi.mock("@/features/node-settings/hooks/useRuntimeAcquisitionHub", () => ({
	useRuntimeAcquisitionHub: (enabled: boolean) => {
		hooksMock.enabledArg = enabled;
		return hooksMock.status;
	},
}));

vi.mock("@/features/node-settings/queries/useLocalRuntime", () => ({
	useEnsureLlamaCppBinary: () => ({ mutate: hooksMock.ensureMutate, isPending: false }),
}));

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { RuntimeAcquisitionBanner } from "@/features/node-settings/components/RuntimeAcquisitionBanner";

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

function status(overrides: Partial<RuntimeAcquisitionStatus> & { sequence: number; phase: string }): RuntimeAcquisitionStatus {
	return { variant: "cuda", tag: "b9692", completedBytes: null, totalBytes: null, stepIndex: 1, stepCount: 1, ...overrides };
}

function renderBanner(): void {
	render(
		<MantineProvider>
			<RuntimeAcquisitionBanner />
		</MantineProvider>,
	);
}

describe("RuntimeAcquisitionBanner", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		hooksMock.status = undefined;
		hooksMock.enabledArg = undefined;
		useNodeAuthStore.setState({ accessToken: "token" });
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("renders nothing before any status has arrived", () => {
		renderBanner();
		expect(screen.queryByTestId("runtime-acquisition-banner")).toBeNull();
	});

	it("renders nothing while the host is idle", () => {
		hooksMock.status = status({ sequence: 0, phase: "Idle" });
		renderBanner();
		expect(screen.queryByTestId("runtime-acquisition-banner")).toBeNull();
	});

	it("shows a determinate download with a progress bar, and no dismiss while it runs", () => {
		hooksMock.status = status({ sequence: 3, phase: "Downloading", completedBytes: 5_242_880, totalBytes: 10_485_760 });

		renderBanner();

		expect(screen.getByTestId("runtime-acquisition-banner")).toBeTruthy();
		expect(screen.getByTestId("runtime-acquisition-banner-progress")).toBeTruthy();
		// Non-dismissible while running: the banner is the answer to "why can't I chat yet?".
		expect(screen.queryByTestId("runtime-acquisition-banner-dismiss")).toBeNull();
		expect(screen.queryByTestId("runtime-acquisition-banner-retry")).toBeNull();
	});

	it("stays indeterminate while the download total is still unknown", () => {
		// `totalBytes` is legitimately absent until the response headers land — a fabricated percentage would be a lie.
		hooksMock.status = status({ sequence: 2, phase: "Downloading", completedBytes: 4096, totalBytes: null });

		renderBanner();

		expect(screen.getByTestId("runtime-acquisition-banner")).toBeTruthy();
		expect(screen.queryByTestId("runtime-acquisition-banner-progress")).toBeNull();
	});

	it("shows the step counter only on the multi-archive path", () => {
		// i18n is not initialized under the suite, so `t` yields the raw default template rather than interpolated copy
		// (same caveat as LlamaCppUpdateBanner.test.tsx). Assert which segments compose the detail line, not their text.
		hooksMock.status = status({ sequence: 4, phase: "Downloading", completedBytes: 1024, totalBytes: 4096, stepIndex: 2, stepCount: 2 });
		renderBanner();
		expect(screen.getByTestId("runtime-acquisition-banner-detail").textContent).toContain("Step {{index}} of {{count}}");

		// A single-archive acquisition must not show "Step 1 of 1" — it would imply a second step that never comes.
		cleanup();
		hooksMock.status = status({ sequence: 5, phase: "Downloading", completedBytes: 1024, totalBytes: 4096 });
		renderBanner();
		expect(screen.getByTestId("runtime-acquisition-banner-detail").textContent).not.toContain("Step");
	});

	it("hides once acquisition completes", () => {
		hooksMock.status = status({ sequence: 7, phase: "Completed" });
		renderBanner();
		expect(screen.queryByTestId("runtime-acquisition-banner")).toBeNull();
	});

	it("STAYS visible on failure with the sanitized reason and a working retry", () => {
		// The whole point of the change: hiding on every terminal phase would make an offline first run look identical to
		// a merely slow one.
		hooksMock.status = status({ sequence: 8, phase: "Failed", sanitizedError: "The runtime archive could not be downloaded." });

		renderBanner();

		expect(screen.getByTestId("runtime-acquisition-banner")).toBeTruthy();
		expect(screen.getByText("The runtime archive could not be downloaded.")).toBeTruthy();

		fireEvent.click(screen.getByTestId("runtime-acquisition-banner-retry"));
		// Re-ensures the variant the failed acquisition was targeting, not a silent downgrade to cpu.
		expect(hooksMock.ensureMutate).toHaveBeenCalledWith("cuda");
	});

	it("falls back to a generic reason when the failure carries no sanitized message", () => {
		hooksMock.status = status({ sequence: 9, phase: "Failed", sanitizedError: null, variant: null });

		renderBanner();

		expect(screen.getByTestId("runtime-acquisition-banner")).toBeTruthy();
		fireEvent.click(screen.getByTestId("runtime-acquisition-banner-retry"));
		// A probe that failed before choosing a variant leaves it null; cpu is the always-available fallback.
		expect(hooksMock.ensureMutate).toHaveBeenCalledWith("cpu");
	});

	it("is dismissible from the failed state, and re-shows for a later failure", () => {
		hooksMock.status = status({ sequence: 8, phase: "Failed", sanitizedError: "Boom." });
		renderBanner();

		fireEvent.click(screen.getByTestId("runtime-acquisition-banner-dismiss"));
		expect(screen.queryByTestId("runtime-acquisition-banner")).toBeNull();

		// A retry that fails again arrives under a higher sequence, so the dismissal does not silence it.
		cleanup();
		hooksMock.status = status({ sequence: 12, phase: "Failed", sanitizedError: "Boom again." });
		renderBanner();
		expect(screen.getByTestId("runtime-acquisition-banner")).toBeTruthy();
	});

	it("keeps both the hydrate query and the hub subscription off until the client is authenticated", () => {
		useNodeAuthStore.setState({ accessToken: undefined });

		renderBanner();

		expect(hooksMock.enabledArg).toBe(false);
	});
});
