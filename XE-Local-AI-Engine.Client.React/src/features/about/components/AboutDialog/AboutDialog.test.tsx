// @vitest-environment jsdom

import "@/i18n";

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Stub out the app-update section so the existing tests are not affected by the
// QueryClient requirement it introduces.
vi.mock("@/features/app-update/components/AppUpdateSection", () => ({
	AppUpdateSection: () => null,
}));

import { AboutDialog } from "@/features/about/components/AboutDialog/AboutDialog";
import { applicationInfo } from "@/features/about/data/AboutData";

function renderWithProviders(ui: ReactElement) {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	function Wrapper({ children }: { children: ReactNode }) {
		return (
			<QueryClientProvider client={queryClient}>
				<MantineProvider>{children}</MantineProvider>
			</QueryClientProvider>
		);
	}
	return render(ui, { wrapper: Wrapper });
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
		// Version is injected at build time from Directory.Build.props, so assert against the live source rather than a
		// hardcoded literal that breaks on every version bump.
		expect(within(dialog).getByText(applicationInfo.version)).toBeTruthy();
		expect(within(dialog).getByText(/Local AI engine for running, managing, and chatting/)).toBeTruthy();
	});

	it("renders generated frontend and backend packages with a source type", () => {
		renderWithProviders(<AboutDialog opened={true} onClose={vi.fn()} />);

		fireEvent.click(screen.getByRole("tab", { name: "Licenses" }));

		// react (npm) and Serilog (NuGet) come from the generated license list, proving
		// both the frontend and backend sources are rendered.
		expect(screen.getByText("react")).toBeTruthy();
		expect(screen.getByText("Serilog")).toBeTruthy();
		expect(screen.getAllByText("Frontend").length).toBeGreaterThan(0);
		expect(screen.getAllByText("Backend").length).toBeGreaterThan(0);
	});

	it("filters the third-party license table by query", () => {
		renderWithProviders(<AboutDialog opened={true} onClose={vi.fn()} />);

		fireEvent.click(screen.getByRole("tab", { name: "Licenses" }));
		expect(screen.getByText("react")).toBeTruthy();
		expect(screen.getByText("Serilog")).toBeTruthy();

		fireEvent.change(screen.getByPlaceholderText("Search packages"), { target: { value: "serilog" } });

		expect(screen.getByText("Serilog")).toBeTruthy();
		expect(screen.queryByText("react")).toBeNull();
	});
});
