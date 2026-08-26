import { MantineProvider } from "@mantine/core";
import { render } from "@testing-library/react";
import type { ReactElement } from "react";
import { vi } from "vitest";

// MantineProvider reads the color scheme through matchMedia on mount, several components measure themselves
// through ResizeObserver, and an autosize <Textarea> subscribes to `document.fonts` ("loadingdone" re-measures
// after a web font swaps in). jsdom implements none of the three, so every Mantine render needs these stubs
// installed first — without them the provider (or the first autosize Textarea) throws before the component under
// test ever renders. The FontFaceSet stub is why an autosize Textarea no longer fails with
// "Cannot read properties of undefined (reading 'addEventListener')".
export function installJsdomEnvironmentMocks(): void {
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
	// jsdom has no layout engine, so Element.prototype.scrollIntoView does not exist. Mantine's combobox calls it on a
	// TIMER after the dropdown opens, which lands after the test that opened it has finished — an uncaught exception
	// rather than a failed assertion, and one that fails the run without naming a broken expectation.
	Object.defineProperty(Element.prototype, "scrollIntoView", {
		writable: true,
		configurable: true,
		value: vi.fn(),
	});
	Object.defineProperty(document, "fonts", {
		writable: true,
		configurable: true,
		value: {
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			ready: Promise.resolve(),
		},
	});
}

// Renders a component inside a bare MantineProvider, the minimum context every Mantine component needs.
export function renderWithMantine(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}
