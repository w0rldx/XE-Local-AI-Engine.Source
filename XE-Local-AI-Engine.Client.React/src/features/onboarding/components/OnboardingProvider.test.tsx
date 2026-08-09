// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { joyrideProps, onEventRef, stateRef, markDoneMock, modelsRef } = vi.hoisted(() => ({
	joyrideProps: { current: null as null | { run: boolean; stepIndex: number; steps: { title?: string }[] } },
	onEventRef: { current: null as null | ((data: Record<string, unknown>) => void) },
	stateRef: {
		current: { isResolved: true, isSuccess: true, statusByKey: {} as Record<string, "completed" | "skipped" | undefined> },
	},
	markDoneMock: vi.fn(),
	modelsRef: {
		current: { data: undefined, isSuccess: false } as {
			data?: { items?: { modelName: string; kind?: string }[]; selectedModelName?: string | null };
			isSuccess: boolean;
		},
	},
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

vi.mock("@tanstack/react-query", () => ({ useQuery: () => modelsRef.current }));
vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({ listLocalModelsOptions: () => ({}) }));
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
import { tutorialProgressStorageKey } from "@/features/onboarding/hooks/useTourState";

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
	return render(<MantineProvider><OnboardingProvider>app</OnboardingProvider></MantineProvider>);
}

beforeEach(() => {
	installDomMocks();
	localStorage.clear();
	joyrideProps.current = null;
	stateRef.current = { isResolved: true, isSuccess: true, statusByKey: {} };
	modelsRef.current = { data: undefined, isSuccess: false };
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
		["ready", { data: { items: [{ modelName: "chat", kind: "Chat" }], selectedModelName: "chat" }, isSuccess: true }, "navChat"],
		["installed-unselected", { data: { items: [{ modelName: "chat", kind: "Chat" }] }, isSuccess: true }, "setDefaultModel"],
		["missing", { data: { items: [], selectedModelName: null }, isSuccess: true }, "navModels"],
		["unresolved", { data: undefined, isSuccess: false }, "navModels"],
	])("selects the canonical first step for %s", (_name, query, firstStepId) => {
		modelsRef.current = query;
		renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		expect(joyrideProps.current?.steps[0]?.title).toContain(`.${firstStepId}.title`);
		const frozenLength = joyrideProps.current?.steps.length;
		modelsRef.current = { data: { items: [{ modelName: "chat", kind: "Chat" }], selectedModelName: "chat" }, isSuccess: true };
		expect(joyrideProps.current?.steps.length).toBe(frozenLength);
	});

	it("finishes the frozen flow as completed and clears progress", () => {
		modelsRef.current = { data: { items: [{ modelName: "chat", kind: "Chat" }], selectedModelName: "chat" }, isSuccess: true };
		renderProvider();
		fireEvent.click(screen.getByTestId("onboarding-welcome-start"));
		act(() => onEventRef.current?.({ type: EVENTS.STEP_AFTER, action: ACTIONS.NEXT, index: 3, status: STATUS.RUNNING }));
		expect(markDoneMock).toHaveBeenCalledWith("main-app-v1", "completed");
		expect(localStorage.getItem(tutorialProgressStorageKey("main-app-v1"))).toBeNull();
	});
});
