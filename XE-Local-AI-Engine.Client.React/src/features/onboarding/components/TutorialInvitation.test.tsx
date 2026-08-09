// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { TutorialInvitation } from "@/features/onboarding/components/TutorialInvitation";
import { OnboardingContext, type OnboardingContextValue } from "@/features/onboarding/context/OnboardingContext";

vi.mock("react-i18next", () => ({ useTranslation: () => ({ t: (key: string) => key }) }));

function context(overrides: Partial<OnboardingContextValue> = {}): OnboardingContextValue {
	return {
		isStateResolved: true,
		isStateSuccessful: true,
		activeTutorialId: null,
		tutorials: {
			"quick-start": { isAvailable: true, hasProgress: false },
			"agents-basics": { isAvailable: true, hasProgress: false },
			"knowledge-base-basics": { isAvailable: true, hasProgress: false },
		},
		start: vi.fn(),
		resume: vi.fn(),
		restart: vi.fn(),
		dismiss: vi.fn(),
		...overrides,
	};
}

function renderInvitation(value: OnboardingContextValue) {
	return render(
		<MantineProvider>
			<OnboardingContext.Provider value={value}>
				<TutorialInvitation tutorialId="agents-basics" />
			</OnboardingContext.Provider>
		</MantineProvider>,
	);
}

beforeEach(() => {
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
});

afterEach(cleanup);

describe("TutorialInvitation", () => {
	it("shows only after a successful absent-state read and starts on explicit click", () => {
		const value = context();
		renderInvitation(value);
		expect(screen.getByTestId("tutorial-invitation-agents-basics")).toBeTruthy();
		expect(value.start).not.toHaveBeenCalled();
		fireEvent.click(screen.getByRole("button", { name: "onboarding.actions.start" }));
		expect(value.start).toHaveBeenCalledWith("agents-basics");
	});

	it("suppresses skipped and completed invitations", () => {
		const skipped = context({
			tutorials: { ...context().tutorials, "agents-basics": { isAvailable: true, hasProgress: false, status: "skipped" } },
		});
		const view = renderInvitation(skipped);
		expect(screen.queryByTestId("tutorial-invitation-agents-basics")).toBeNull();
		view.rerender(
			<MantineProvider>
				<OnboardingContext.Provider value={context({
					tutorials: { ...context().tutorials, "agents-basics": { isAvailable: true, hasProgress: false, status: "completed" } },
				})}>
					<TutorialInvitation tutorialId="agents-basics" />
				</OnboardingContext.Provider>
			</MantineProvider>,
		);
		expect(screen.queryByTestId("tutorial-invitation-agents-basics")).toBeNull();
	});

	it("suppresses contextual invitations while any tutorial is active", () => {
		renderInvitation(context({ activeTutorialId: "quick-start" }));

		expect(screen.queryByTestId("tutorial-invitation-agents-basics")).toBeNull();
	});

	it("Not now persists skipped and a saved step changes the CTA to Resume", () => {
		const dismiss = vi.fn();
		const resume = vi.fn();
		const value = context({
			dismiss,
			resume,
			tutorials: { ...context().tutorials, "agents-basics": { isAvailable: true, hasProgress: true } },
		});
		renderInvitation(value);
		fireEvent.click(screen.getByRole("button", { name: "onboarding.actions.resume" }));
		expect(resume).toHaveBeenCalledWith("agents-basics");
		fireEvent.click(screen.getByRole("button", { name: "onboarding.actions.notNow" }));
		expect(dismiss).toHaveBeenCalledWith("agents-basics");
	});
});
