// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
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

import { ToolCategoryBadge } from "@/core/tools/components/ToolCategoryBadge/ToolCategoryBadge";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

describe("ToolCategoryBadge", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders a ReadLocal tool as auto-executing (no approval shield)", () => {
		renderWithMantine(<ToolCategoryBadge category="ReadLocal" effectiveRequiresApproval={false} />);

		const badge = screen.getByTestId("tool-category-badge-ReadLocal");
		expect(badge.textContent).toContain("read-only");
		expect(badge.getAttribute("data-requires-approval")).toBe("false");
	});

	it("renders a Network tool as approval-required", () => {
		renderWithMantine(<ToolCategoryBadge category="Network" effectiveRequiresApproval={true} />);

		const badge = screen.getByTestId("tool-category-badge-Network");
		expect(badge.textContent).toContain("network");
		expect(badge.getAttribute("data-requires-approval")).toBe("true");
	});

	it("renders an Unknown tool fail-closed (approval-required)", () => {
		renderWithMantine(<ToolCategoryBadge category="Unknown" effectiveRequiresApproval={true} />);

		const badge = screen.getByTestId("tool-category-badge-Unknown");
		expect(badge.textContent).toContain("uncategorized");
		expect(badge.getAttribute("data-requires-approval")).toBe("true");
	});
});
