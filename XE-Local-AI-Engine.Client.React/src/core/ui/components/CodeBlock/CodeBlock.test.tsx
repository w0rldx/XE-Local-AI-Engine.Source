// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { CodeBlock } from "@/core/ui/components/CodeBlock/CodeBlock";
import { renderWithMantine } from "@/test/MantineTestRender";

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
		renderWithMantine(<CodeBlock language="json" code={code} />);

		expect(screen.getByText(/"time"/)).toBeTruthy();
		expect(screen.getByLabelText("Copy code")).toBeTruthy();
	});

	it("renders non-JSON text verbatim", () => {
		const { container } = renderWithMantine(<CodeBlock language="json" code="plain text" />);

		expect(container.textContent).toContain("plain text");
	});
});
