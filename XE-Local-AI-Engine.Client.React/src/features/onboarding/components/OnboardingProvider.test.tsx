// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Capture the props Joyride is rendered with so we can assert `run` stays false on skip without rendering the real
// overlay (which needs a live DOM target + portals).
const { joyrideProps, markDoneMock, shouldPromptRef, onEventRef } = vi.hoisted(() => ({
	joyrideProps: { current: null as null | { run: boolean; stepIndex: number } },
	markDoneMock: vi.fn(),
	shouldPromptRef: { current: true },
	onEventRef: { current: null as null | ((data: Record<string, unknown>) => void) },
}));

// Replace only the Joyride component (rendering the real overlay needs live DOM targets + portals); keep the real
// ACTIONS/EVENTS/STATUS constants the provider imports so the mock can't drift from the library.
vi.mock("react-joyride", async (importOriginal) => {
	const actual = await importOriginal<typeof import("react-joyride")>();
	return {
		...actual,
		Joyride: (props: { run: boolean; stepIndex: number; onEvent?: (data: Record<string, unknown>) => void }) => {
			joyrideProps.current = { run: props.run, stepIndex: props.stepIndex };
			onEventRef.current = props.onEvent ?? null;
			return null;
		},
	};
});

vi.mock("@/features/onboarding/hooks/useTourState", () => ({
	useTourState: () => ({ shouldPrompt: shouldPromptRef.current, isResolved: true, markDone: markDoneMock }),
}));

// The provider reads the installed-models query — stub it so the component renders without the real
// generated client / a live QueryClient.
vi.mock("@tanstack/react-query", () => ({
	useQuery: () => ({ data: undefined }),
	useQueryClient: () => ({
		getQueriesData: () => [],
		getQueryCache: () => ({
			subscribe: () => () => undefined,
			findAll: () => [],
		}),
	}),
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	listLocalModelsOptions: () => ({ queryKey: ["listLocalModels"] }),
}));

vi.mock("@/core/integrations/tanstack-router/Router", () => ({
	router: {
		state: { location: { pathname: "/" } },
		navigate: vi.fn().mockResolvedValue(undefined),
	},
}));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, defaultValue?: string) => defaultValue ?? _key }),
}));

vi.mock("@mantine/notifications", () => ({
	notifications: { show: vi.fn() },
}));

import { ACTIONS, EVENTS, STATUS } from "react-joyride";
import { OnboardingProvider } from "@/features/onboarding/components/OnboardingProvider";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

// Mantine reads matchMedia/ResizeObserver on mount; jsdom provides neither, so stub them (same shape the other
// component tests in this repo install).
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

beforeEach(() => {
	installJsdomEnvironmentMocks();
	shouldPromptRef.current = true;
	joyrideProps.current = null;
	onEventRef.current = null;
});

afterEach(() => {
	cleanup();
	vi.clearAllMocks();
});

describe("OnboardingProvider welcome gate", () => {
	it("offers the welcome dialog on first run and records 'skipped' without starting the tour", () => {
		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);

		// The opt-in dialog is shown when no tour state is recorded.
		const skipButton = screen.getByTestId("onboarding-welcome-skip");
		expect(joyrideProps.current?.run).toBe(false);

		fireEvent.click(skipButton);

		// Skip persists the skipped outcome and the tour never starts (run stays false — plan acceptance criteria).
		expect(markDoneMock).toHaveBeenCalledWith("skipped");
		expect(joyrideProps.current?.run).toBe(false);
	});

	it("starts the controlled tour at step 0 when the user accepts", () => {
		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);

		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));

		expect(joyrideProps.current?.run).toBe(true);
		expect(joyrideProps.current?.stepIndex).toBe(0);
		expect(markDoneMock).not.toHaveBeenCalled();
	});

	it("does not prompt when the tour state is already recorded", () => {
		shouldPromptRef.current = false;
		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);

		expect(screen.queryByTestId("onboarding-welcome-skip")).toBeNull();
		expect(joyrideProps.current?.run).toBe(false);
	});
});

describe("OnboardingProvider event handling", () => {
	it("PREV on an async step advances backward (Back works on async steps)", () => {
		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		expect(joyrideProps.current?.run).toBe(true);

		// Manually advance to the install step (index 1) by firing STEP_AFTER Next on step 0.
		act(() => {
			onEventRef.current?.({
				type: EVENTS.STEP_AFTER,
				action: ACTIONS.NEXT,
				index: 0,
				status: STATUS.RUNNING,
			});
		});
		expect(joyrideProps.current?.stepIndex).toBe(1);

		// PREV on the async install step (index 1) must go back to index 0.
		act(() => {
			onEventRef.current?.({
				type: EVENTS.STEP_AFTER,
				action: ACTIONS.PREV,
				index: 1,
				status: STATUS.RUNNING,
			});
		});
		expect(joyrideProps.current?.stepIndex).toBe(0);
	});

	it("NEXT on an async step is a no-op (tour waits for real-state advance)", () => {
		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));

		// Advance to install step.
		act(() => {
			onEventRef.current?.({
				type: EVENTS.STEP_AFTER,
				action: ACTIONS.NEXT,
				index: 0,
				status: STATUS.RUNNING,
			});
		});
		const indexAtInstall = joyrideProps.current?.stepIndex;

		// NEXT on the async step is swallowed — stepIndex must not change.
		act(() => {
			onEventRef.current?.({
				type: EVENTS.STEP_AFTER,
				action: ACTIONS.NEXT,
				index: 1,
				status: STATUS.RUNNING,
			});
		});
		expect(joyrideProps.current?.stepIndex).toBe(indexAtInstall);
		expect(markDoneMock).not.toHaveBeenCalled();
	});

	it("TARGET_NOT_FOUND retries navigation and does not immediately finish the tour", () => {
		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));

		act(() => {
			onEventRef.current?.({
				type: EVENTS.TARGET_NOT_FOUND,
				action: ACTIONS.UPDATE,
				index: 1,
				status: STATUS.RUNNING,
			});
		});

		// After first TARGET_NOT_FOUND the tour must still be running (not finished/skipped).
		expect(joyrideProps.current?.run).toBe(true);
		expect(markDoneMock).not.toHaveBeenCalled();
	});

	it("TARGET_NOT_FOUND after MAX_TARGET_RETRIES finishes the tour as skipped", () => {
		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));

		// Fire TARGET_NOT_FOUND 3 times (MAX_TARGET_RETRIES = 2 means 3rd fires finish).
		for (let i = 0; i < 3; i++) {
			act(() => {
				onEventRef.current?.({
					type: EVENTS.TARGET_NOT_FOUND,
					action: ACTIONS.UPDATE,
					index: 1,
					status: STATUS.RUNNING,
				});
			});
		}

		expect(markDoneMock).toHaveBeenCalledWith("skipped");
	});
});
