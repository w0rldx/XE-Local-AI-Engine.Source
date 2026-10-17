// @vitest-environment jsdom

import { act, cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { waitSeconds } = vi.hoisted(() => ({ waitSeconds: vi.fn(() => 0) }));

vi.mock("@/core/auth/api/NodeAuthApi", () => ({ refreshRetryWaitSeconds: waitSeconds }));

import { NavigationLoadingIndicator } from "@/core/ui/components/NavigationLoadingIndicator/NavigationLoadingIndicator";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

describe("NavigationLoadingIndicator", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.useFakeTimers();
	});

	afterEach(() => {
		cleanup();
		vi.useRealTimers();
		waitSeconds.mockReset();
		waitSeconds.mockReturnValue(0);
	});

	it("shows only the loading line when no refresh is waiting out a rate limit", () => {
		renderWithMantine(<NavigationLoadingIndicator />);

		expect(screen.getByText("Loading...")).toBeTruthy();
		expect(screen.queryByTestId("navigation-loading-rate-limited")).toBeNull();
	});

	it("counts down the Retry-After wait of a rate-limited boot and drops the line when it ends", () => {
		waitSeconds.mockReturnValue(42);
		renderWithMantine(<NavigationLoadingIndicator />);

		expect(screen.getByTestId("navigation-loading-rate-limited").textContent).toBe(
			"Too many requests to the node. Retrying in 42 s.",
		);

		waitSeconds.mockReturnValue(41);
		act(() => {
			vi.advanceTimersByTime(1_000);
		});
		expect(screen.getByTestId("navigation-loading-rate-limited").textContent).toBe(
			"Too many requests to the node. Retrying in 41 s.",
		);

		waitSeconds.mockReturnValue(0);
		act(() => {
			vi.advanceTimersByTime(1_000);
		});
		expect(screen.queryByTestId("navigation-loading-rate-limited")).toBeNull();
	});
});
