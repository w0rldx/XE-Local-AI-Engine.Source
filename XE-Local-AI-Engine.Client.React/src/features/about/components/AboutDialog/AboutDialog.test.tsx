// @vitest-environment jsdom

import "@/i18n";

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { AboutDialog } from "@/features/about/components/AboutDialog/AboutDialog";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("AboutDialog", () => {
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
	});

	it("shows application info on the Application tab", () => {
		renderWithProviders(<AboutDialog opened={true} onClose={vi.fn()} />);

		const dialog = screen.getByRole("dialog");
		expect(within(dialog).getByText("0.1.0")).toBeTruthy();
		expect(within(dialog).getByText(/Local AI engine worker node/)).toBeTruthy();
	});

	it("filters the third-party license table by query", () => {
		renderWithProviders(<AboutDialog opened={true} onClose={vi.fn()} />);

		fireEvent.click(screen.getByRole("tab", { name: "Licenses" }));
		expect(screen.getByText("zustand")).toBeTruthy();
		expect(screen.getByText("React")).toBeTruthy();

		fireEvent.change(screen.getByPlaceholderText("Search packages"), { target: { value: "zustand" } });

		expect(screen.getByText("zustand")).toBeTruthy();
		expect(screen.queryByText("React")).toBeNull();
	});
});
