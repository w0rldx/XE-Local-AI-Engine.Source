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
	AppUpdateSection: () => <div data-testid="app-update-lifecycle" />,
}));

import { AboutDialog } from "@/features/about/components/AboutDialog/AboutDialog";
import { OnboardingContext, type OnboardingContextValue } from "@/features/onboarding/context/OnboardingContext";
import {
	applicationInfo,
	runtimeLegalDocumentsForUserAgent,
} from "@/features/about/data/AboutData";

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

function renderWithOnboarding(ui: ReactElement, onboarding: OnboardingContextValue) {
	return renderWithProviders(<OnboardingContext.Provider value={onboarding}>{ui}</OnboardingContext.Provider>);
}

function createOnboardingContext(): OnboardingContextValue {
	return {
		isStateResolved: true,
		isStateSuccessful: true,
		tutorials: {
			"quick-start": { isAvailable: true, hasProgress: false },
			"agents-basics": { isAvailable: true, hasProgress: true },
			"knowledge-base-basics": { isAvailable: true, hasProgress: false, status: "completed" },
		},
		start: vi.fn(),
		resume: vi.fn(),
		restart: vi.fn(),
		dismiss: vi.fn(),
	};
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

	it("keeps the update lifecycle mounted while the dialog is hidden", () => {
		const { rerender } = renderWithProviders(<AboutDialog opened={true} onClose={vi.fn()} />);
		expect(screen.getByTestId("app-update-lifecycle")).toBeTruthy();

		rerender(<AboutDialog opened={false} onClose={vi.fn()} />);

		expect(screen.getByTestId("app-update-lifecycle")).toBeTruthy();
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

	it("shows exactly the three optional tutorials in a dedicated tab", () => {
		renderWithProviders(<AboutDialog opened={true} onClose={vi.fn()} />);
		fireEvent.click(screen.getByRole("tab", { name: "Tutorials" }));
		expect(screen.getByTestId("tutorial-card-quick-start")).toBeTruthy();
		expect(screen.getByTestId("tutorial-card-agents-basics")).toBeTruthy();
		expect(screen.getByTestId("tutorial-card-knowledge-base-basics")).toBeTruthy();
		expect(screen.getAllByTestId(/^tutorial-card-/)).toHaveLength(3);
	});

	it("opens the controlled Tutorials tab from Application and uses the state-derived action", () => {
		const onboarding = createOnboardingContext();
		const onClose = vi.fn();
		renderWithOnboarding(<AboutDialog opened={true} onClose={onClose} />, onboarding);

		fireEvent.click(screen.getByTestId("about-open-tutorials"));
		expect(screen.getByRole("tab", { name: "Tutorials" }).getAttribute("aria-selected")).toBe("true");
		const agentsCard = screen.getByTestId("tutorial-card-agents-basics");
		fireEvent.click(within(agentsCard).getByRole("button", { name: "Resume" }));
		expect(onboarding.resume).toHaveBeenCalledWith("agents-basics");
		expect(onClose).toHaveBeenCalledOnce();
	});

	it("links every bundled runtime license and notice from the Licenses tab", () => {
		renderWithProviders(<AboutDialog opened={true} onClose={vi.fn()} />);

		fireEvent.click(screen.getByRole("tab", { name: "Licenses" }));

		expect(screen.getByRole("link", { name: ".NET runtime license" }).getAttribute("href"))
			.toBe("/licenses/dotnet/DOTNET-RUNTIME-LICENSE.txt");
		expect(screen.getByRole("link", { name: ".NET runtime third-party notices" })).toBeTruthy();
		expect(screen.getByRole("link", { name: "ASP.NET Core runtime license" })).toBeTruthy();
		expect(screen.getByRole("link", { name: "ASP.NET Core runtime third-party notices" })).toBeTruthy();
		expect(screen.queryByRole("link", { name: /Library License/ })).toBeNull();
	});

	it("selects only the MIT apphost terms for a Windows framework-dependent package", () => {
		expect(runtimeLegalDocumentsForUserAgent("Mozilla/5.0 (Windows NT 10.0; Win64; x64)"))
			.toEqual([
				{ name: ".NET Windows apphost license", href: "/licenses/dotnet/DOTNET-APPHOST-LICENSE.txt" },
				{
					name: ".NET Windows apphost third-party notices",
					href: "/licenses/dotnet/DOTNET-APPHOST-THIRD-PARTY-NOTICES.txt",
				},
			]);
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
