// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { LlamaCppRuntimeStatus } from "@/features/node-settings/models/LocalRuntimeModels";

const { hooksMock, navigateMock } = vi.hoisted(() => ({
	hooksMock: { statusData: undefined as LlamaCppRuntimeStatus | undefined },
	navigateMock: vi.fn(),
}));

vi.mock("@/features/node-settings/queries/useLocalRuntime", () => ({
	useLlamaCppRuntimeStatus: () => ({ data: hooksMock.statusData }),
}));

vi.mock("@tanstack/react-router", () => ({
	useNavigate: () => navigateMock,
}));

import { LlamaCppUpdateBanner } from "@/features/node-settings/components/LlamaCppUpdateBanner";
import { useRuntimeUpdateBannerStore } from "@/features/node-settings/stores/RuntimeUpdateBannerStore";
import { renderWithProviders } from "@/test/RenderWithProviders";

function renderBanner(): void {
	renderWithProviders(<LlamaCppUpdateBanner />);
}

describe("LlamaCppUpdateBanner", () => {
	beforeEach(() => {
		hooksMock.statusData = undefined;
		useRuntimeUpdateBannerStore.setState({ dismissedTag: null });
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("renders nothing when no update is available", () => {
		hooksMock.statusData = {
			installed: null,
			recommendedTag: "b9692",
			upstreamLatestTag: null,
			updateAvailable: false,
			isOffline: false,
			runningProcessCount: 0,
		};

		renderBanner();
		expect(screen.queryByTestId("llamacpp-update-banner")).toBeNull();
	});

	it("shows the banner when an update is available and deep-links to node-settings", () => {
		hooksMock.statusData = {
			installed: null,
			recommendedTag: "b9692",
			upstreamLatestTag: null,
			updateAvailable: true,
			isOffline: false,
			runningProcessCount: 0,
		};

		renderBanner();
		// The banner renders (the recommended tag is interpolated via i18n at runtime; under the test i18n stub the
		// raw key template stands, so we assert presence + the deep-link behavior rather than the interpolated text).
		expect(screen.getByTestId("llamacpp-update-banner")).toBeTruthy();

		fireEvent.click(screen.getByTestId("llamacpp-update-banner-cta"));
		expect(navigateMock).toHaveBeenCalledWith({ to: "/node-settings" });
	});

	it("hides the banner after the operator dismisses the current tag", () => {
		hooksMock.statusData = {
			installed: null,
			recommendedTag: "b9692",
			upstreamLatestTag: null,
			updateAvailable: true,
			isOffline: false,
			runningProcessCount: 0,
		};

		renderBanner();
		fireEvent.click(screen.getByTestId("llamacpp-update-banner-dismiss"));
		expect(useRuntimeUpdateBannerStore.getState().dismissedTag).toBe("b9692");

		cleanup();
		renderBanner();
		expect(screen.queryByTestId("llamacpp-update-banner")).toBeNull();
	});
});
