// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { joyrideProps, onEventRef, stateRef, markDoneMock, modelsRef, conversationsRef, toastErrorMock } = vi.hoisted(() => ({
	joyrideProps: { current: null as null | { run: boolean; stepIndex: number; steps: { title?: string }[] } },
	onEventRef: { current: null as null | ((data: Record<string, unknown>) => void) },
	stateRef: {
		current: { isResolved: true, isSuccess: true, statusByKey: {} as Record<string, "completed" | "skipped" | undefined> },
	},
	markDoneMock: vi.fn(),
	modelsRef: {
		current: { data: undefined, isSuccess: false } as {
			data?: {
				isAvailable: boolean;
				items?: { modelName: string; kind?: string; provider?: string }[];
				selectedModelName?: string | null;
			};
			isSuccess: boolean;
		},
	},
	conversationsRef: {
		current: [] as [readonly unknown[], { messages?: { role: string; content: string }[] }][],
	},
	toastErrorMock: vi.fn(),
}));

vi.mock("react-joyride", async (importOriginal) => {
	const actual = await importOriginal<typeof import("react-joyride")>();
	return {
		...actual,
		Joyride: (props: { run: boolean; stepIndex: number; steps: { title?: string }[]; onEvent?: (data: Record<string, unknown>) => void }) => {
			joyrideProps.current = { run: props.run, stepIndex: props.stepIndex, steps: props.steps };
			onEventRef.current = props.onEvent ?? null;
			return null;
		},
	};
});

vi.mock("@/features/onboarding/hooks/useTourState", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/features/onboarding/hooks/useTourState")>()),
	useTutorialState: () => ({ ...stateRef.current, markDone: markDoneMock }),
}));

vi.mock("@tanstack/react-query", () => ({
	useQuery: () => modelsRef.current,
	useQueryClient: () => ({
		getQueriesData: () => conversationsRef.current,
		getQueryCache: () => ({ subscribe: () => () => undefined }),
	}),
}));
vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({ listLocalModelsOptions: () => ({}) }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: { error: toastErrorMock } }));
vi.mock("@/core/integrations/tanstack-router/Router", () => ({
	router: { state: { location: { pathname: "/" } }, navigate: vi.fn().mockResolvedValue(undefined) },
}));
vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string) => key,
		i18n: { changeLanguage: vi.fn().mockResolvedValue(undefined) },
	}),
}));

import { ACTIONS, EVENTS, STATUS } from "react-joyride";
import { OnboardingProvider } from "@/features/onboarding/components/OnboardingProvider";
import { type OnboardingContextValue, useOnboarding } from "@/features/onboarding/context/OnboardingContext";
import { tutorialProgressStorageKey } from "@/features/onboarding/hooks/useTourState";

const contextRef = { current: null as OnboardingContextValue | null };

function ContextCapture() {
	contextRef.current = useOnboarding();
	return null;
}

function installDomMocks() {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn(() => ({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() })),
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

function renderProvider() {
	return render(<MantineProvider><OnboardingProvider><ContextCapture />app</OnboardingProvider></MantineProvider>);
}

function applyModelsQuery(view: ReturnType<typeof renderProvider>, query: typeof modelsRef.current) {
	modelsRef.current = query;
	act(() => {
		view.rerender(<MantineProvider><OnboardingProvider><ContextCapture />app</OnboardingProvider></MantineProvider>);
	});
}

function applyConversations(
	view: ReturnType<typeof renderProvider>,
	conversations: typeof conversationsRef.current,
) {
	conversationsRef.current = conversations;
	act(() => {
		view.rerender(<MantineProvider><OnboardingProvider><ContextCapture />app</OnboardingProvider></MantineProvider>);
	});
}

beforeEach(() => {
	installDomMocks();
	localStorage.clear();
	joyrideProps.current = null;
	contextRef.current = null;
	stateRef.current = { isResolved: true, isSuccess: true, statusByKey: {} };
	modelsRef.current = { data: undefined, isSuccess: false };
	conversationsRef.current = [];
	markDoneMock.mockImplementation(
		(_key: string, _status: string, callbacks?: { onSuccess?: () => void }) => callbacks?.onSuccess?.(),
	);
});

afterEach(() => {
	cleanup();
	vi.clearAllMocks();
});

describe("explicit opt-in", () => {
	it("never auto-starts when progress exists and changes the invitation CTA to Resume", () => {
		localStorage.setItem(tutorialProgressStorageKey("main-app-v1"), JSON.stringify({ format: 1, stepId: "chatInput" }));
		renderProvider();
		expect(joyrideProps.current?.run).toBe(false);
		expect(screen.getByTestId("onboarding-welcome-start").textContent).toBe("onboarding.actions.resume");
	});

	it("starts only after the explicit Start click", () => {
		renderProvider();
		expect(joyrideProps.current?.run).toBe(false);
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		expect(joyrideProps.current?.run).toBe(true);
	});
});

describe("frozen Quick Start readiness", () => {
	it.each([
		["ready", { data: { isAvailable: true, items: [{ modelName: "chat", kind: "Chat" }], selectedModelName: "chat" }, isSuccess: true }, "navChat"],
		["installed-unselected", { data: { isAvailable: true, items: [{ modelName: "chat", kind: "Chat" }] }, isSuccess: true }, "setDefaultModel"],
		["missing", { data: { isAvailable: true, items: [], selectedModelName: null }, isSuccess: true }, "navModels"],
		[
			"cloud-only",
			{
				data: {
					isAvailable: true,
					items: [{ modelName: "gpt-5.3-codex", kind: "Chat", provider: "CodexOAuth" }],
					selectedModelName: "gpt-5.3-codex",
				},
				isSuccess: true,
			},
			"navModels",
		],
		[
			"unavailable with cloud entries",
			{
				data: {
					isAvailable: false,
					items: [{ modelName: "gpt-5.3-codex", kind: "Chat", provider: "CodexOAuth" }],
					selectedModelName: "gpt-5.3-codex",
				},
				isSuccess: true,
			},
			"navModels",
		],
		["unresolved", { data: undefined, isSuccess: false }, "navModels"],
	])("selects the canonical first step for %s", (_name, query, firstStepId) => {
		modelsRef.current = query;
		const view = renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		expect(joyrideProps.current?.steps[0]?.title).toContain(`.${firstStepId}.title`);
		const frozenLength = joyrideProps.current?.steps.length;
		applyModelsQuery(view, {
			data: { isAvailable: true, items: [{ modelName: "chat", kind: "Chat" }], selectedModelName: "chat" },
			isSuccess: true,
		});
		expect(joyrideProps.current?.steps.length).toBe(frozenLength);
	});

	it("finishes the frozen flow as completed and clears progress", () => {
		modelsRef.current = { data: { isAvailable: true, items: [{ modelName: "chat", kind: "Chat" }], selectedModelName: "chat" }, isSuccess: true };
		renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.NEXT, index: 3, status: STATUS.RUNNING }));
		expect(markDoneMock).toHaveBeenCalledWith(
			"main-app-v1",
			"completed",
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);
		expect(localStorage.getItem(tutorialProgressStorageKey("main-app-v1"))).toBeNull();
	});

	it("advances within the frozen plan when model installation and default selection complete", () => {
		modelsRef.current = { data: { isAvailable: true, items: [], selectedModelName: null }, isSuccess: true };
		const view = renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.NEXT, index: 0, status: STATUS.RUNNING }));
		expect(joyrideProps.current?.stepIndex).toBe(1);

		applyModelsQuery(view, { data: { isAvailable: true, items: [{ modelName: "chat", kind: "Chat" }] }, isSuccess: true });
		expect(joyrideProps.current?.stepIndex).toBe(2);

		applyModelsQuery(view, {
			data: { isAvailable: true, items: [{ modelName: "chat", kind: "Chat" }], selectedModelName: "chat" },
			isSuccess: true,
		});
		expect(joyrideProps.current?.stepIndex).toBe(3);
	});

	it("skips the already-satisfied default step when install and selection resolve together", () => {
		modelsRef.current = { data: { isAvailable: true, items: [], selectedModelName: null }, isSuccess: true };
		const view = renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.NEXT, index: 0, status: STATUS.RUNNING }));

		applyModelsQuery(view, {
			data: { isAvailable: true, items: [{ modelName: "chat", kind: "Chat" }], selectedModelName: "chat" },
			isSuccess: true,
		});

		expect(joyrideProps.current?.stepIndex).toBe(3);
	});

	it.each([
		[
			"the install and default steps",
			{ data: { isAvailable: true, items: [{ modelName: "chat", kind: "Chat" }], selectedModelName: "chat" }, isSuccess: true },
			3,
		],
		[
			"only the install step",
			{ data: { isAvailable: true, items: [{ modelName: "chat", kind: "Chat" }] }, isSuccess: true },
			2,
		],
	])("skips %s when readiness changes before entering the setup step", (_name, query, expectedIndex) => {
		modelsRef.current = { data: { isAvailable: true, items: [], selectedModelName: null }, isSuccess: true };
		const view = renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));

		applyModelsQuery(view, query);
		expect(joyrideProps.current?.stepIndex).toBe(0);
		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.NEXT, index: 0, status: STATUS.RUNNING }));

		expect(joyrideProps.current?.stepIndex).toBe(expectedIndex);
	});

	it("completes when a slow assistant reply arrives after entering the final step", () => {
		modelsRef.current = {
			data: { isAvailable: true, items: [{ modelName: "chat", kind: "Chat" }], selectedModelName: "chat" },
			isSuccess: true,
		};
		conversationsRef.current = [[["conversations", "existing"], { messages: [{ role: "assistant", content: "Earlier" }] }]];
		const view = renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		for (let index = 0; index < 3; index += 1) {
			act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.NEXT, index, status: STATUS.RUNNING }));
		}
		expect(joyrideProps.current?.stepIndex).toBe(3);
		expect(markDoneMock).not.toHaveBeenCalled();

		applyConversations(view, [
			[["conversations", "existing"], { messages: [{ role: "assistant", content: "Earlier" }] }],
			[["conversations", "new"], { messages: [{ role: "assistant", content: "Hello!" }] }],
		]);

		expect(markDoneMock).toHaveBeenCalledWith(
			"main-app-v1",
			"completed",
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);
		expect(joyrideProps.current?.run).toBe(false);
	});

	it("completes when a fast assistant reply arrives before entering the final step", () => {
		modelsRef.current = {
			data: { isAvailable: true, items: [{ modelName: "chat", kind: "Chat" }], selectedModelName: "chat" },
			isSuccess: true,
		};
		conversationsRef.current = [[["conversations", "existing"], { messages: [{ role: "assistant", content: "Earlier" }] }]];
		const view = renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		for (let index = 0; index < 2; index += 1) {
			act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.NEXT, index, status: STATUS.RUNNING }));
		}
		expect(joyrideProps.current?.stepIndex).toBe(2);

		applyConversations(view, [
			[["conversations", "existing"], { messages: [{ role: "assistant", content: "Earlier" }] }],
			[["conversations", "new"], { messages: [{ role: "assistant", content: "Hello!" }] }],
		]);
		expect(markDoneMock).not.toHaveBeenCalled();

		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.NEXT, index: 2, status: STATUS.RUNNING }));

		expect(markDoneMock).toHaveBeenCalledWith(
			"main-app-v1",
			"completed",
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);
		expect(joyrideProps.current?.run).toBe(false);
	});

	it("does not downgrade a same-session completion when its replay is closed", () => {
		modelsRef.current = {
			data: { isAvailable: true, items: [{ modelName: "chat", kind: "Chat" }], selectedModelName: "chat" },
			isSuccess: true,
		};
		renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.NEXT, index: 3, status: STATUS.RUNNING }));
		expect(markDoneMock).toHaveBeenCalledTimes(1);
		expect(markDoneMock).toHaveBeenLastCalledWith(
			"main-app-v1",
			"completed",
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);

		act(() => contextRef.current?.restart("quick-start"));
		expect(joyrideProps.current?.run).toBe(true);
		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.CLOSE, index: 0, status: STATUS.RUNNING }));

		expect(markDoneMock).toHaveBeenCalledTimes(1);
		expect(contextRef.current?.tutorials["quick-start"].status).toBe("completed");
	});

	it("keeps completed when consecutive completion and skip saves resolve out of order", () => {
		const callbacks: Array<{
			status: string;
			onSuccess?: () => void;
		}> = [];
		markDoneMock.mockImplementation(
			(_key: string, status: string, resultCallbacks?: { onSuccess?: () => void }) => {
				callbacks.push({ status, onSuccess: resultCallbacks?.onSuccess });
			},
		);
		modelsRef.current = {
			data: { isAvailable: true, items: [{ modelName: "chat", kind: "Chat" }], selectedModelName: "chat" },
			isSuccess: true,
		};
		renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.NEXT, index: 3, status: STATUS.RUNNING }));
		act(() => contextRef.current?.restart("quick-start"));
		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.CLOSE, index: 0, status: STATUS.RUNNING }));

		expect(callbacks.map((entry) => entry.status)).toEqual(["completed", "skipped"]);
		act(() => callbacks[1]?.onSuccess?.());
		expect(contextRef.current?.tutorials["quick-start"].status).toBe("skipped");
		act(() => callbacks[0]?.onSuccess?.());
		expect(contextRef.current?.tutorials["quick-start"].status).toBe("completed");
	});

	it("lets a persisted completion override an earlier local skip", () => {
		const view = renderProvider();
		act(() => contextRef.current?.dismiss("agents-basics"));
		expect(contextRef.current?.tutorials["agents-basics"].status).toBe("skipped");

		stateRef.current = {
			isResolved: true,
			isSuccess: true,
			statusByKey: { "agents-v1": "completed" },
		};
		act(() => {
			view.rerender(<MantineProvider><OnboardingProvider><ContextCapture />app</OnboardingProvider></MantineProvider>);
		});

		expect(contextRef.current?.tutorials["agents-basics"].status).toBe("completed");
	});

	it("does not replace an already active tutorial", () => {
		modelsRef.current = { data: { isAvailable: true, items: [], selectedModelName: null }, isSuccess: true };
		renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		expect(contextRef.current?.activeTutorialId).toBe("quick-start");

		act(() => contextRef.current?.start("agents-basics"));

		expect(contextRef.current?.activeTutorialId).toBe("quick-start");
		expect(joyrideProps.current?.steps[0]?.title).toContain(".navModels.title");
	});

	it("reports a failed terminal-state save without presenting it as persisted", () => {
		markDoneMock.mockImplementation(
			(_key: string, _status: string, callbacks?: { onError?: () => void }) => callbacks?.onError?.(),
		);
		renderProvider();

		act(() => contextRef.current?.dismiss("agents-basics"));

		expect(contextRef.current?.tutorials["agents-basics"].status).toBeUndefined();
		expect(toastErrorMock).toHaveBeenCalledWith("onboarding.errors.saveState");
	});
});

describe("controlled tutorial recovery", () => {
	beforeEach(() => {
		modelsRef.current = { data: { isAvailable: true, items: [], selectedModelName: null }, isSuccess: true };
	});

	it("keeps Back available on an action step", () => {
		renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.NEXT, index: 0, status: STATUS.RUNNING }));
		expect(joyrideProps.current?.stepIndex).toBe(1);

		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.PREV, index: 1, status: STATUS.RUNNING }));

		expect(joyrideProps.current?.stepIndex).toBe(0);
	});

	it("advances after bounded missing-target retries instead of dead-ending", () => {
		renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.NEXT, index: 0, status: STATUS.RUNNING }));

		for (let attempt = 0; attempt <= 4; attempt += 1) {
			act(() =>
				onEventRef.current?.({ type: EVENTS.TARGET_NOT_FOUND, action: ACTIONS.UPDATE, index: 1, status: STATUS.RUNNING }),
			);
		}

		expect(joyrideProps.current?.run).toBe(true);
		expect(joyrideProps.current?.stepIndex).toBe(2);
		expect(markDoneMock).not.toHaveBeenCalled();
	});

	it("records skipped and stops when an incomplete tutorial is closed", () => {
		renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));

		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.CLOSE, index: 0, status: STATUS.RUNNING }));

		expect(markDoneMock).toHaveBeenCalledWith(
			"main-app-v1",
			"skipped",
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);
		expect(joyrideProps.current?.run).toBe(false);
		expect(localStorage.getItem(tutorialProgressStorageKey("main-app-v1"))).toBeNull();
	});
});
