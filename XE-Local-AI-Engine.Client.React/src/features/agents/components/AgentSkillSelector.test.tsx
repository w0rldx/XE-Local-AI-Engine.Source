// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { SkillSummary } from "@/features/skills/models/SkillModels";

const { useSkillsMock } = vi.hoisted(() => ({
	useSkillsMock: vi.fn(),
}));

vi.mock("@/features/skills/queries/useSkills", () => ({
	useSkills: useSkillsMock,
}));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? _key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
}));

import { AgentSkillSelector } from "@/features/agents/components/AgentSkillSelector";

const skillDefaults = {
	allowedTools: null,
	compatibility: null,
	createdAtUtc: 0,
	importedAtUtc: null,
	license: null,
	metadata: null,
	origin: "Local",
	sourceUri: null,
	updatedAtUtc: 0,
} as const satisfies Partial<SkillSummary>;

const library: SkillSummary[] = [
	{
		...skillDefaults,
		id: "skill-1",
		name: "invoice-review",
		description: "How to review supplier invoices.",
		enabled: true,
		version: 1,
	},
	{
		...skillDefaults,
		id: "skill-2",
		name: "legal-redline",
		description: "How to redline a contract.",
		enabled: false,
		version: 2,
	},
];

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

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
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
}

function renderSelector(selectedSkillIds: string[] = []) {
	const onToggleSkill = vi.fn();
	renderWithProviders(<AgentSkillSelector selectedSkillIds={selectedSkillIds} onToggleSkill={onToggleSkill} />);
	return { onToggleSkill };
}

describe("AgentSkillSelector", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useSkillsMock.mockReturnValue({ data: library, isLoading: false, error: null });
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("lists the node skill library and flags disabled skills", () => {
		renderSelector();

		expect(screen.getByTestId("agent-skill-row-skill-1")).toBeTruthy();
		expect(screen.getByTestId("agent-skill-row-skill-2")).toBeTruthy();
		// The disabled skill carries a disabled badge.
		expect(screen.getByText("disabled")).toBeTruthy();
	});

	it("reflects the selected ids as checked checkboxes (persists selection)", () => {
		renderSelector(["skill-1"]);

		const checked = screen.getByTestId("agent-skill-checkbox-skill-1") as HTMLInputElement;
		const unchecked = screen.getByTestId("agent-skill-checkbox-skill-2") as HTMLInputElement;
		expect(checked.checked).toBe(true);
		expect(unchecked.checked).toBe(false);
	});

	it("invokes onToggleSkill with (id, true) when an unselected skill is checked", () => {
		const { onToggleSkill } = renderSelector();

		fireEvent.click(screen.getByTestId("agent-skill-checkbox-skill-1"));

		expect(onToggleSkill).toHaveBeenCalledWith("skill-1", true);
	});

	it("invokes onToggleSkill with (id, false) when a selected skill is unchecked", () => {
		const { onToggleSkill } = renderSelector(["skill-1"]);

		fireEvent.click(screen.getByTestId("agent-skill-checkbox-skill-1"));

		expect(onToggleSkill).toHaveBeenCalledWith("skill-1", false);
	});

	it("still renders a selected skill that is no longer in the library so it can be deselected", () => {
		renderSelector(["removed-skill"]);

		expect(screen.getByTestId("agent-skill-row-removed-skill")).toBeTruthy();
		const checkbox = screen.getByTestId("agent-skill-checkbox-removed-skill") as HTMLInputElement;
		expect(checkbox.checked).toBe(true);
	});

	it("shows a loading state while the library is fetching", () => {
		useSkillsMock.mockReturnValue({ data: undefined, isLoading: true, error: null });

		renderSelector();

		expect(screen.getByTestId("agent-skill-loading")).toBeTruthy();
	});

	it("shows an empty hint when the library has no skills", () => {
		useSkillsMock.mockReturnValue({ data: [], isLoading: false, error: null });

		renderSelector();

		expect(screen.getByTestId("agent-skill-empty")).toBeTruthy();
	});
});
