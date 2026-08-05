// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

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

import { SkillList } from "@/features/skills/components/SkillList";
import type { SkillSummary } from "@/features/skills/models/SkillModels";

const base = {
	allowedTools: null,
	compatibility: null,
	createdAtUtc: 0,
	description: "d",
	enabled: true,
	importedAtUtc: null,
	license: null,
	metadata: null,
	origin: "Local",
	sourceUri: null,
	updatedAtUtc: 0,
	version: 1,
} as const satisfies Omit<SkillSummary, "id" | "name">;

function renderList(skills: SkillSummary[]) {
	render(
		<MantineProvider>
			<SkillList skills={skills} isMutating={false} onEdit={vi.fn()} onDelete={vi.fn()} />
		</MantineProvider>,
	);
}

describe("SkillList", () => {
	beforeEach(() => {
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
			})),
		});
		// Table.ScrollContainer uses a ResizeObserver, which jsdom does not implement.
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

	it("badges an imported skill with its source and leaves a local one unbadged", () => {
		renderList([
			{ ...base, id: "s1", name: "invoice-review", origin: "Imported", sourceUri: "github:microsoft/skills" },
			{ ...base, id: "s2", name: "legal-redline" },
		]);

		expect(screen.getByTestId("skill-imported-badge-s1").textContent).toContain("github:microsoft/skills");
		expect(screen.queryByTestId("skill-imported-badge-s2")).toBeNull();
	});

	it("names the source as unknown rather than dropping the badge when the source did not survive", () => {
		renderList([{ ...base, id: "s3", name: "mystery", origin: "Imported" }]);

		expect(screen.getByTestId("skill-imported-badge-s3").textContent).toContain("an unknown source");
	});

	// Such a row is dropped when an agent is built, so the operator has to be told to rename it — otherwise the skill
	// silently does nothing forever.
	it("flags a stored name the resolver would reject", () => {
		renderList([
			{ ...base, id: "s4", name: "bad--name" },
			{ ...base, id: "s5", name: "good-name" },
		]);

		expect(screen.getByTestId("skill-invalid-name-s4")).toBeTruthy();
		expect(screen.queryByTestId("skill-invalid-name-s5")).toBeNull();
	});

	// The list projection cannot populate a resource count, so a badge here would read a constant zero.
	it("shows no resource-count column", () => {
		renderList([{ ...base, id: "s6", name: "invoice-review" }]);

		expect(screen.queryByText(/resources/i)).toBeNull();
	});
});
