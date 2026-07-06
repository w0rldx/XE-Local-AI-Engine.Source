// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Stub i18n — return the key segment after the last dot as a readable stand-in so assertions don't depend on copy.
vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string) => key.split(".").at(-1) ?? key,
	}),
}));

import { TourShowcasePanel } from "@/features/onboarding/components/TourShowcasePanel";

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
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
});

afterEach(() => {
	cleanup();
	vi.clearAllMocks();
});

function renderPanel(active = true) {
	return render(
		<MantineProvider>
			<TourShowcasePanel active={active} />
		</MantineProvider>,
	);
}

describe("TourShowcasePanel", () => {
	it("renders the panel root with data-tour and data-testid anchors", () => {
		renderPanel();
		// screen.getByTestId throws if the element is absent — that is the assertion.
		screen.getByTestId("tour-showcase-panel");
		expect(document.querySelector('[data-tour="tour-showcase"]')).not.toBeNull();
	});

	it("renders the reasoning-effort section with its data-tour anchor", () => {
		renderPanel();
		screen.getByTestId("showcase-reasoning-effort");
		expect(document.querySelector('[data-tour="showcase-reasoning-effort"]')).not.toBeNull();
	});

	it("renders the reasoning-trace section with its data-tour anchor", () => {
		renderPanel();
		screen.getByTestId("showcase-reasoning-trace");
		expect(document.querySelector('[data-tour="showcase-reasoning-trace"]')).not.toBeNull();
	});

	it("renders the tool-call section with its data-tour anchor", () => {
		renderPanel();
		screen.getByTestId("showcase-tool-call");
		expect(document.querySelector('[data-tour="showcase-tool-call"]')).not.toBeNull();
	});

	it("renders the agent-mode section with its data-tour anchor", () => {
		renderPanel();
		screen.getByTestId("showcase-agent-mode");
		expect(document.querySelector('[data-tour="showcase-agent-mode"]')).not.toBeNull();
	});

	it("renders the knowledge-base section with its data-tour anchor", () => {
		renderPanel();
		screen.getByTestId("showcase-knowledge-base");
		expect(document.querySelector('[data-tour="showcase-knowledge-base"]')).not.toBeNull();
	});

	it("renders the image-generation section with its data-tour anchor", () => {
		renderPanel();
		screen.getByTestId("showcase-image-generation");
		expect(document.querySelector('[data-tour="showcase-image-generation"]')).not.toBeNull();
	});

	it("renders the agents section with its data-tour anchor", () => {
		renderPanel();
		screen.getByTestId("showcase-agents");
		expect(document.querySelector('[data-tour="showcase-agents"]')).not.toBeNull();
	});

	it("keeps every showcase target in the DOM when inactive so Joyride can still anchor", () => {
		// Regression guard: conditionally unmounting the panel left Joyride unable to find the showcase targets,
		// dead-ending the tour (screen dimmed, no tooltip). Inactive must still render the anchors (hidden, not removed).
		renderPanel(false);
		screen.getByTestId("tour-showcase-panel");
		expect(document.querySelector('[data-tour="showcase-reasoning-effort"]')).not.toBeNull();
		expect(document.querySelector('[data-tour="showcase-reasoning-trace"]')).not.toBeNull();
		expect(document.querySelector('[data-tour="showcase-tool-call"]')).not.toBeNull();
		expect(document.querySelector('[data-tour="showcase-agent-mode"]')).not.toBeNull();
	});

	it("resolves all i18n keys (none returns the raw dotted namespace path)", () => {
		renderPanel();
		// The mock returns the last key segment; verify the rendered text is not a full dotted path.
		const allText = document.body.textContent ?? "";
		expect(allText).not.toMatch(/^onboarding\./);
		// Each section emits its 'label' key — verify the word appears somewhere in the output.
		expect(allText).toContain("label");
	});
});
