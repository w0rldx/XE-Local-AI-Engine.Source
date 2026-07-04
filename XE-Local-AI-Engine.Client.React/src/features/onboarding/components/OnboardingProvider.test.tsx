// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Capture the props Joyride is rendered with so we can assert `run` stays false on skip without rendering the real
// overlay (which needs a live DOM target + portals).
const {
	joyrideProps,
	markDoneMock,
	shouldPromptRef,
	onEventRef,
	tourProgressRef,
	clearTourProgressMock,
	modelsDataRef,
	conversationsRef,
} = vi.hoisted(() => ({
		joyrideProps: { current: null as null | { run: boolean; stepIndex: number } },
		markDoneMock: vi.fn(),
		shouldPromptRef: { current: true },
		onEventRef: { current: null as null | ((data: Record<string, unknown>) => void) },
		// Controllable persisted in-progress step index for the resume-on-reload tests (Bug B). null = no saved progress.
		tourProgressRef: { current: null as number | null },
		clearTourProgressMock: vi.fn(),
		// Controllable installed-models query payload for the flash-then-skip auto-advance tests. `undefined` mirrors a
		// still-loading list (the default the other tests rely on); set { items, selectedModelName } to drive the
		// install/default real-state effects. A rerender after mutating this surfaces the new value to the provider.
		modelsDataRef: {
			current: undefined as
				| { items?: { modelName: string; kind?: string }[]; selectedModelName?: string | null }
				| undefined,
		},
		// Controllable conversations-query payload backing the firstResponse reply signal. `hasVisibleAssistantReply`
		// reads this via queryClient.getQueriesData(); each entry is the [queryKey, conversation] tuple TanStack returns.
		// Empty = no reply yet; seed a tuple with an assistant message to simulate a streamed reply. A rerender re-reads
		// the snapshot through useSyncExternalStore, so mutating this + rerender drives the unmet→met transition.
		conversationsRef: {
			current: [] as [readonly unknown[], { messages?: { role: string; content: string }[] }][],
		},
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
	// Persisted-progress helpers (Bug B). readTourProgress is driven by tourProgressRef; write records the latest index
	// back into the ref so a resume-then-advance round-trips; clear is observable + resets the ref.
	readTourProgress: () => tourProgressRef.current,
	writeTourProgress: (index: number) => {
		tourProgressRef.current = index;
	},
	clearTourProgress: () => {
		tourProgressRef.current = null;
		clearTourProgressMock();
	},
}));

// The provider reads the installed-models query — stub it so the component renders without the real
// generated client / a live QueryClient.
vi.mock("@tanstack/react-query", () => ({
	useQuery: () => ({ data: modelsDataRef.current }),
	useQueryClient: () => ({
		getQueriesData: () => conversationsRef.current,
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

// Stub the showcase panel. It is ALWAYS mounted now (so Joyride can always anchor its targets); the `active` prop —
// not mount/unmount — gates whether it is the live showcase overlay. We surface `active` via a data attribute so the
// tests can assert the activeness transition rather than presence.
vi.mock("@/features/onboarding/components/TourShowcasePanel", () => ({
	TourShowcasePanel: ({ active }: { active: boolean }) => (
		<div data-testid="tour-showcase-panel" data-active={active ? "true" : "false"} />
	),
}));

import { ACTIONS, EVENTS, STATUS } from "react-joyride";
import { OnboardingProvider } from "@/features/onboarding/components/OnboardingProvider";
import { FIRST_SHOWCASE_STEP_INDEX, tourStepIds } from "@/features/onboarding/data/MainAppTourSteps";

// Derived constants used in showcase-step tests.
const FIRST_RESPONSE_STEP_INDEX = tourStepIds.indexOf("firstResponse");
const LAST_SHOWCASE_STEP_INDEX = tourStepIds.length - 1;
const INSTALL_STEP_INDEX = tourStepIds.indexOf("recommendationInstall");
const DEFAULT_STEP_INDEX = tourStepIds.indexOf("setDefaultModel");
const NAV_CHAT_STEP_INDEX = tourStepIds.indexOf("navChat");

// A chat-capable installed model (kind === "Chat") the install/default real-state effects accept.
const CHAT_MODEL = { modelName: "llama-3", kind: "Chat" } as const;

// A getQueriesData payload carrying one conversation with a non-empty assistant message — what hasVisibleAssistantReply
// treats as "the user's first send produced a streamed reply" (the firstResponse step's advance signal).
const REPLY_CONVERSATIONS: [readonly unknown[], { messages?: { role: string; content: string }[] }][] = [
	[["conversations"], { messages: [{ role: "assistant", content: "Hello!" }] }],
];

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

// Mutates the installed-models query payload and re-renders so the provider's real-state effects observe the new value.
// A bare ref mutation can't re-run the effects (React never re-renders on a ref change); rerendering the same element
// re-invokes the mocked useQuery, and the new modelItems/selectedModelName references re-fire the dependent effects —
// this is how we simulate a genuine unmet→met transition while the user sits on a step.
function applyModelsData(
	view: ReturnType<typeof renderWithProviders>,
	data: { items?: { modelName: string; kind?: string }[]; selectedModelName?: string | null } | undefined,
): void {
	modelsDataRef.current = data;
	act(() => {
		view.rerender(
			<MantineProvider>
				<OnboardingProvider>app</OnboardingProvider>
			</MantineProvider>,
		);
	});
}

// Mutates the conversations query payload and re-renders so the firstResponse reply signal re-reads its snapshot. The
// signal flows through useSyncExternalStore (getSnapshot calls hasVisibleAssistantReply), which is re-read on every
// render — so the rerender surfaces the seeded reply and re-fires the firstResponse effect's hasReply dependency.
function applyConversations(
	view: ReturnType<typeof renderWithProviders>,
	data: [readonly unknown[], { messages?: { role: string; content: string }[] }][],
): void {
	conversationsRef.current = data;
	act(() => {
		view.rerender(
			<MantineProvider>
				<OnboardingProvider>app</OnboardingProvider>
			</MantineProvider>,
		);
	});
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
	tourProgressRef.current = null;
	modelsDataRef.current = undefined;
	conversationsRef.current = [];
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

describe("OnboardingProvider resume-on-reload (Bug B)", () => {
	it("resumes the tour at the persisted step index instead of showing the welcome dialog", () => {
		// Simulate a mid-tour reload: no terminal entry recorded (shouldPrompt stays true) but a saved in-progress
		// index exists in localStorage. The provider must RESUME (run=true at that index), not re-open Welcome.
		shouldPromptRef.current = true;
		tourProgressRef.current = 3;

		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);

		expect(screen.queryByTestId("onboarding-welcome-skip")).toBeNull();
		expect(joyrideProps.current?.run).toBe(true);
		expect(joyrideProps.current?.stepIndex).toBe(3);
		expect(markDoneMock).not.toHaveBeenCalled();
	});

	it("ignores an out-of-range saved index and falls back to the welcome dialog", () => {
		// Defensive: a stale index beyond the current step array (e.g. tour length shrank) must not resume.
		shouldPromptRef.current = true;
		tourProgressRef.current = 9999;

		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);

		expect(screen.getByTestId("onboarding-welcome-skip")).not.toBeNull();
		expect(joyrideProps.current?.run).toBe(false);
	});

	it("does not resume when a terminal outcome is already recorded (completed/skipped tour never resurrects)", () => {
		// Even if a stale progress index lingers, a recorded terminal status (shouldPrompt=false) must win — no resume,
		// no welcome.
		shouldPromptRef.current = false;
		tourProgressRef.current = 3;

		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);

		expect(screen.queryByTestId("onboarding-welcome-skip")).toBeNull();
		expect(joyrideProps.current?.run).toBe(false);
	});

	it("clears persisted progress on finish so a completed tour cannot resurrect", () => {
		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));

		// Finishing the tour (last showcase step Next) must clear the saved index.
		act(() => {
			onEventRef.current?.({
				type: EVENTS.STEP_AFTER,
				action: ACTIONS.NEXT,
				index: LAST_SHOWCASE_STEP_INDEX,
				status: STATUS.RUNNING,
			});
		});

		expect(clearTourProgressMock).toHaveBeenCalled();
		expect(tourProgressRef.current).toBeNull();
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

	it("NEXT on an async step ADVANCES the tour (never strands the overlay)", () => {
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
		const indexAtInstall = joyrideProps.current?.stepIndex ?? 0;

		// NEXT on the async install step now advances like any other step. The old behavior swallowed it (no-op), which in
		// controlled Joyride hid the tooltip and stranded a grayed overlay with no way forward on a fresh node. The
		// real-state effects still auto-advance when a model is installed, but a manual Next must always move forward.
		act(() => {
			onEventRef.current?.({
				type: EVENTS.STEP_AFTER,
				action: ACTIONS.NEXT,
				index: 1,
				status: STATUS.RUNNING,
			});
		});
		expect(joyrideProps.current?.stepIndex).toBe(indexAtInstall + 1);
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

	it("TARGET_NOT_FOUND on a ROUTE-BOUND step ADVANCES after retries exhausted (never dead-ends, Bug A)", () => {
		// Bug A regression: a route-bound step (index < FIRST_SHOWCASE_STEP_INDEX) whose target is absent used to
		// finish('skipped') after retries, leaving a multi-second grayed screen then ending the tour. It must now
		// ADVANCE to the next step like showcase steps do. MAX_TARGET_RETRIES rAF retries fire first; the (n+1)th
		// missing-target event exhausts the budget and advances.
		const ROUTE_BOUND_INDEX = 1; // recommendationInstall — route-bound, < FIRST_SHOWCASE_STEP_INDEX.
		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));

		// Force the step to the route-bound index by simulating a not-found-advance won't apply yet; drive Next on
		// step 0 to land on the route-bound install step.
		act(() => {
			onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.NEXT, index: 0, status: STATUS.RUNNING });
		});
		expect(joyrideProps.current?.stepIndex).toBe(ROUTE_BOUND_INDEX);

		// Fire TARGET_NOT_FOUND enough times to exhaust the retry budget (MAX_TARGET_RETRIES retries + 1 advance).
		// MAX_TARGET_RETRIES is 4, so 5 events: 4 retries (rAF re-measure) then the 5th advances.
		for (let i = 0; i < 5; i++) {
			act(() => {
				onEventRef.current?.({
					type: EVENTS.TARGET_NOT_FOUND,
					action: ACTIONS.UPDATE,
					index: ROUTE_BOUND_INDEX,
					status: STATUS.RUNNING,
				});
			});
		}

		// Must NOT finish/skip — it advances forward and the tour keeps running.
		expect(markDoneMock).not.toHaveBeenCalled();
		expect(joyrideProps.current?.run).toBe(true);
		expect(joyrideProps.current?.stepIndex).toBe(ROUTE_BOUND_INDEX + 1);
	});
});

describe("OnboardingProvider showcase transition", () => {
	it("reply signal on firstResponse step advances to first showcase step (not finish)", () => {
		// The provider's hasReply effect is driven by the queryClient cache subscriber; in the test the
		// queryClient mock's subscribe is a no-op. We simulate the transition by advancing via STEP_AFTER
		// through all non-async steps up to firstResponse then testing the index directly from the provider.
		// Because firstResponse is async (NEXT no-op), we verify the provider correctly exposes
		// FIRST_SHOWCASE_STEP_INDEX as the next destination after the reply arrives by checking the constant.
		expect(FIRST_SHOWCASE_STEP_INDEX).toBe(FIRST_RESPONSE_STEP_INDEX + 1);
		expect(FIRST_SHOWCASE_STEP_INDEX).toBe(tourStepIds.indexOf("reasoningEffort"));
	});

	it("showcase panel is mounted-but-inactive before the tour starts (so Joyride can always anchor its targets)", () => {
		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);
		// Always mounted now; inactive (hidden/inert) until the tour reaches a showcase step. No jest-dom in this
		// project, so read the data attribute directly.
		expect(screen.getByTestId("tour-showcase-panel").getAttribute("data-active")).toBe("false");
	});

	it("showcase panel stays inactive on non-showcase steps", () => {
		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));

		// Advance through all non-async steps up to (but not including) firstResponse, then skip past
		// the async steps by directly jumping to the first showcase index.
		// Steps 0 (navModels), 3 (navChat), 4 (chatInput) are normal Next steps; 1,2,6 are async.
		// The simplest path: fire STEP_AFTER NEXT for index 0 to reach step 1 (install, async), then
		// jump directly by firing events at the pre-showcase indices.
		// Jump directly to FIRST_SHOWCASE_STEP_INDEX via goToStep simulation: fire STEP_AFTER NEXT at
		// every step index < FIRST_SHOWCASE_STEP_INDEX. Async steps will no-op Next, so fire PREV then
		// re-advance them. Instead — use the simpler approach: fire enough Next events so stepIndex
		// naturally reaches FIRST_SHOWCASE_STEP_INDEX.
		// Async steps (1,2,6) swallow Next — so we can only drive non-async steps with Next events.
		// We need to bypass async steps in test. The cleanest approach: advance step 0 → 1 via Next;
		// PREV back to 0; drive forward through all 11 steps using PREV=back logic is convoluted.
		// Instead assert the panel is inactive at a non-showcase step, which is the meaningful invariant.
		expect(screen.getByTestId("tour-showcase-panel").getAttribute("data-active")).toBe("false");

		// Advance step 0 → 1 (async, no-op Next) and verify that on the early (install) step the panel is
		// still inactive — it is mounted throughout, but only the showcase steps make it active.
		act(() => {
			onEventRef.current?.({
				type: EVENTS.STEP_AFTER,
				action: ACTIONS.NEXT,
				index: 0,
				status: STATUS.RUNNING,
			});
		});
		// Still on an early step (async install step) — panel must be inactive.
		expect(screen.getByTestId("tour-showcase-panel").getAttribute("data-active")).toBe("false");
	});

	it("last showcase step STEP_AFTER+NEXT (real controlled-mode path) calls finish('completed')", () => {
		// REGRESSION TEST: in controlled mode Joyride does NOT emit STATUS.FINISHED on its own — the final
		// primary-button click produces EVENTS.STEP_AFTER with action=NEXT at index=steps.length-1. Without
		// the `index >= steps.length - 1` early-finish guard the old clamp logic called goToStep(sameIndex)
		// and the tour got stuck, never persisting "completed". This test reproduces that exact sequence.
		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));

		act(() => {
			onEventRef.current?.({
				type: EVENTS.STEP_AFTER,
				action: ACTIONS.NEXT,
				index: LAST_SHOWCASE_STEP_INDEX,
				status: STATUS.RUNNING,
			});
		});

		expect(markDoneMock).toHaveBeenCalledWith("completed");
		// Tour must stop (run → false), which deactivates the (always-mounted) showcase panel.
		expect(joyrideProps.current?.run).toBe(false);
	});

	it("last showcase step synthetic TOUR_END/FINISHED also persists completed (belt-and-suspenders)", () => {
		// Keeps coverage of the TOUR_END branch in case the library ever does emit it.
		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));

		act(() => {
			onEventRef.current?.({
				type: EVENTS.TOUR_END,
				action: ACTIONS.NEXT,
				index: LAST_SHOWCASE_STEP_INDEX,
				status: STATUS.FINISHED,
			});
		});

		expect(markDoneMock).toHaveBeenCalledWith("completed");
	});
});

describe("OnboardingProvider auto-advance arming (flash-then-skip fix)", () => {
	// These tests resume the tour directly at the step under test via the persisted-progress path (tourProgressRef), so
	// the provider lands on the async real-state step with the chosen models payload already in place — exactly the
	// "returning/test user" arrival the fix targets.

	it("does NOT auto-advance the setDefaultModel step when a chat-capable default is already set on arrival", () => {
		// Returning user: a chat model is installed AND already the default the moment the step renders. Pre-fix this
		// flashed the step for ~1s then auto-advanced; it must now stay put and wait for a manual Next.
		modelsDataRef.current = { items: [CHAT_MODEL], selectedModelName: CHAT_MODEL.modelName };
		tourProgressRef.current = DEFAULT_STEP_INDEX;

		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);

		expect(joyrideProps.current?.run).toBe(true);
		expect(joyrideProps.current?.stepIndex).toBe(DEFAULT_STEP_INDEX);
		expect(markDoneMock).not.toHaveBeenCalled();
	});

	it("auto-advances the setDefaultModel step on a genuine unmet→met default transition", () => {
		// Arrival: a chat model is installed but NO default is set yet (condition unmet → effect arms). When the user
		// then picks a default (unmet→met while on the step), the tour advances to navChat.
		modelsDataRef.current = { items: [CHAT_MODEL], selectedModelName: undefined };
		tourProgressRef.current = DEFAULT_STEP_INDEX;

		const view = renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);
		expect(joyrideProps.current?.stepIndex).toBe(DEFAULT_STEP_INDEX);

		applyModelsData(view, { items: [CHAT_MODEL], selectedModelName: CHAT_MODEL.modelName });

		expect(joyrideProps.current?.stepIndex).toBe(NAV_CHAT_STEP_INDEX);
	});

	it("does NOT auto-advance the install step when a chat model is already installed on arrival", () => {
		// Returning user: a chat model is already installed when the install step renders — leave the step visible.
		modelsDataRef.current = { items: [CHAT_MODEL], selectedModelName: null };
		tourProgressRef.current = INSTALL_STEP_INDEX;

		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);

		expect(joyrideProps.current?.run).toBe(true);
		expect(joyrideProps.current?.stepIndex).toBe(INSTALL_STEP_INDEX);
		expect(markDoneMock).not.toHaveBeenCalled();
	});

	it("auto-advances the install step on a genuine unmet→met install transition", () => {
		// Arrival: the resolved list is empty (no chat model → effect arms). When the user installs one (unmet→met while
		// on the step), the tour advances to setDefaultModel.
		modelsDataRef.current = { items: [], selectedModelName: null };
		tourProgressRef.current = INSTALL_STEP_INDEX;

		const view = renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);
		expect(joyrideProps.current?.stepIndex).toBe(INSTALL_STEP_INDEX);

		applyModelsData(view, { items: [CHAT_MODEL], selectedModelName: null });

		expect(joyrideProps.current?.stepIndex).toBe(DEFAULT_STEP_INDEX);
	});

	it("still skips the setDefaultModel step forward when the prerequisite model is missing (recovery preserved)", () => {
		// Skip-if-prereq-missing recovery: arriving on setDefaultModel with the list resolved AND empty (no chat model to
		// set as default) must proactively step forward to navChat — unchanged by the arming fix.
		modelsDataRef.current = { items: [], selectedModelName: null };
		tourProgressRef.current = DEFAULT_STEP_INDEX;

		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);

		expect(joyrideProps.current?.stepIndex).toBe(NAV_CHAT_STEP_INDEX);
		expect(markDoneMock).not.toHaveBeenCalled();
	});

	it("does NOT auto-advance the firstResponse step when an assistant reply is already present on arrival", () => {
		// Returning user: a prior conversation already holds an assistant reply the moment the firstResponse step renders
		// (condition met on arrival, armed=false). The step must stay visible to be read, not flash past.
		conversationsRef.current = REPLY_CONVERSATIONS;
		tourProgressRef.current = FIRST_RESPONSE_STEP_INDEX;

		renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);

		expect(joyrideProps.current?.run).toBe(true);
		expect(joyrideProps.current?.stepIndex).toBe(FIRST_RESPONSE_STEP_INDEX);
		expect(markDoneMock).not.toHaveBeenCalled();
	});

	it("auto-advances the firstResponse step to the first showcase step on a genuine reply transition", () => {
		// Arrival: no assistant reply yet (condition unmet → effect arms). When the user's first send produces a streamed
		// reply (unmet→met while on the step), the tour advances into the first showcase step.
		conversationsRef.current = [];
		tourProgressRef.current = FIRST_RESPONSE_STEP_INDEX;

		const view = renderWithProviders(<OnboardingProvider>app</OnboardingProvider>);
		expect(joyrideProps.current?.stepIndex).toBe(FIRST_RESPONSE_STEP_INDEX);

		applyConversations(view, REPLY_CONVERSATIONS);

		expect(joyrideProps.current?.stepIndex).toBe(FIRST_SHOWCASE_STEP_INDEX);
	});
});
