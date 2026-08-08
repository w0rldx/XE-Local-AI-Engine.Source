// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { CodeBlock } from "@/core/ui/components/CodeBlock/CodeBlock";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("CodeBlock", () => {
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

	afterEach(() => {
		cleanup();
	});

	it("renders the code text and a copy affordance", () => {
		const code = '{\n  "time": "12:00"\n}';
		renderWithProviders(<CodeBlock language="json" code={code} />);

		expect(screen.getByText(/"time"/)).toBeTruthy();
		expect(screen.getByLabelText("Copy code")).toBeTruthy();
	});

	it("renders non-JSON text verbatim", () => {
		const { container } = renderWithProviders(<CodeBlock language="json" code="plain text" />);

		expect(container.textContent).toContain("plain text");
	});
});
