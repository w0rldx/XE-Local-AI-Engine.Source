// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
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

import { ToolCategoryBadge } from "@/features/tools/components/ToolCategoryBadge";

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

describe("ToolCategoryBadge", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders a ReadLocal tool as auto-executing (no approval shield)", () => {
		renderWithProviders(<ToolCategoryBadge category="ReadLocal" effectiveRequiresApproval={false} />);

		const badge = screen.getByTestId("tool-category-badge-ReadLocal");
		expect(badge.textContent).toContain("read-only");
		expect(badge.getAttribute("data-requires-approval")).toBe("false");
	});

	it("renders a Network tool as approval-required", () => {
		renderWithProviders(<ToolCategoryBadge category="Network" effectiveRequiresApproval={true} />);

		const badge = screen.getByTestId("tool-category-badge-Network");
		expect(badge.textContent).toContain("network");
		expect(badge.getAttribute("data-requires-approval")).toBe("true");
	});

	it("renders an Unknown tool fail-closed (approval-required)", () => {
		renderWithProviders(<ToolCategoryBadge category="Unknown" effectiveRequiresApproval={true} />);

		const badge = screen.getByTestId("tool-category-badge-Unknown");
		expect(badge.textContent).toContain("uncategorized");
		expect(badge.getAttribute("data-requires-approval")).toBe("true");
	});
});
