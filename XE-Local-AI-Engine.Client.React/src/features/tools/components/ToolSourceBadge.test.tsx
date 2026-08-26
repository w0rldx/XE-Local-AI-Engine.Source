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

import { ToolSourceBadge } from "@/features/tools/components/ToolSourceBadge";

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

describe("ToolSourceBadge", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders a built-in badge", () => {
		renderWithProviders(<ToolSourceBadge source={{ kind: "builtin", serverSlug: null }} />);

		const badge = screen.getByTestId("tool-source-badge-builtin");
		expect(badge.textContent).toBe("built-in");
	});

	it("renders an mcp badge with the server slug", () => {
		renderWithProviders(<ToolSourceBadge source={{ kind: "mcp", serverSlug: "filesystem-tools" }} />);

		const badge = screen.getByTestId("tool-source-badge-mcp");
		expect(badge.textContent).toBe("MCP · filesystem-tools");
	});

	it("renders a generic mcp badge when the slug is missing", () => {
		renderWithProviders(<ToolSourceBadge source={{ kind: "mcp", serverSlug: null }} />);

		const badge = screen.getByTestId("tool-source-badge-mcp");
		expect(badge.textContent).toBe("MCP");
	});

	// The badge sits in nowrap rows beside a tool name that will not shrink either. Letting it shrink clipped the label
	// to "BUILT…" on a phone, which reads as a different word rather than as a truncation.
	it.each([
		["builtin", { kind: "builtin", serverSlug: null }],
		["custom", { kind: "custom", serverSlug: null }],
		["mcp", { kind: "mcp", serverSlug: "filesystem-tools" }],
	] as const)("keeps the %s badge from being squeezed by its row", (kind, source) => {
		renderWithProviders(<ToolSourceBadge source={source} />);

		expect(screen.getByTestId(`tool-source-badge-${kind}`).style.flexShrink).toBe("0");
	});
});
