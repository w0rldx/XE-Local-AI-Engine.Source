// @vitest-environment jsdom

import "@/i18n";

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";

function renderWithProviders(ui: ReactElement) {
	return render(
		<MantineProvider>
			<ConfirmProvider>{ui}</ConfirmProvider>
		</MantineProvider>,
	);
}

describe("DialogShell", () => {
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

	// REGRESSION (capture run 2026-08-01): the test id landed on Mantine's Modal ROOT — a zero-size portal wrapper that
	// Playwright resolves but reports as `hidden` — so `waitFor({ state: "visible" })` timed out against a dialog that
	// was plainly on screen. It must land on the visible dialog card instead.
	it("puts data-testid on the visible modal content, not the zero-size portal root", () => {
		renderWithProviders(
			<DialogShell opened={true} onClose={vi.fn()} title="Editor" data-testid="sample-dialog">
				<div>body</div>
			</DialogShell>,
		);

		const target = screen.getByTestId("sample-dialog");
		expect(target.classList.contains("mantine-Modal-content")).toBe(true);
		expect(target.classList.contains("mantine-Modal-root")).toBe(false);

		// The dialog's own content must live inside the tagged element, so a scoped `within(...)` query works.
		expect(within(target).getByText("body")).toBeTruthy();

		// The tagged element IS the dialog. Pinned because the E2E onboarding test used to chain
		// `GetByTestId(...).GetByRole(Dialog)` to escape the un-hittable portal root — with the id on the content that
		// descent would find nothing, so the chain had to go. If Mantine ever moves role="dialog" off the content
		// section, this fails here rather than as a mystifying E2E timeout.
		expect(target.getAttribute("role")).toBe("dialog");
	});

	it("shows a fullscreen toggle that flips between fullscreen and exit-fullscreen", () => {
		renderWithProviders(
			<DialogShell opened={true} onClose={vi.fn()} title="Editor">
				<div>body</div>
			</DialogShell>,
		);

		// Starts windowed: the toggle offers to enter fullscreen.
		const toggle = screen.getByRole("button", { name: "Fullscreen" });
		expect(toggle).toBeTruthy();

		fireEvent.click(toggle);

		// After toggling, the same control now offers to exit fullscreen.
		expect(screen.getByRole("button", { name: "Exit fullscreen" })).toBeTruthy();
		expect(screen.queryByRole("button", { name: "Fullscreen" })).toBeNull();
	});

	it("hides the fullscreen toggle when enableFullScreenToggle is false", () => {
		renderWithProviders(
			<DialogShell opened={true} onClose={vi.fn()} title="Editor" enableFullScreenToggle={false}>
				<div>body</div>
			</DialogShell>,
		);

		expect(screen.queryByRole("button", { name: "Fullscreen" })).toBeNull();
		expect(screen.queryByRole("button", { name: "Exit fullscreen" })).toBeNull();
	});

	it("renders the sticky footer content", () => {
		renderWithProviders(
			<DialogShell opened={true} onClose={vi.fn()} title="Editor" footer={<button type="button">Save</button>}>
				<div>body</div>
			</DialogShell>,
		);

		const dialog = screen.getByRole("dialog");
		expect(within(dialog).getByRole("button", { name: "Save" })).toBeTruthy();
	});

	it("closes directly via the title-bar close button when not guarded", () => {
		const onClose = vi.fn();
		renderWithProviders(
			<DialogShell opened={true} onClose={onClose} title="Editor">
				<div>body</div>
			</DialogShell>,
		);

		fireEvent.click(screen.getByRole("button", { name: "close" }));
		expect(onClose).toHaveBeenCalledTimes(1);
	});

	it("routes a guarded close through the confirm dialog and only closes when confirmed", async () => {
		const onClose = vi.fn();
		renderWithProviders(
			<DialogShell opened={true} onClose={onClose} title="Editor" confirmCloseWhen={true}>
				<div>body</div>
			</DialogShell>,
		);

		fireEvent.click(screen.getByRole("button", { name: "close" }));

		// Confirmation copy appears; onClose is not called yet.
		await waitFor(() => {
			expect(screen.getByText("Discard unsaved changes?")).toBeTruthy();
		});
		expect(onClose).not.toHaveBeenCalled();

		fireEvent.click(screen.getByRole("button", { name: "Discard" }));
		await waitFor(() => {
			expect(onClose).toHaveBeenCalledTimes(1);
		});
	});

	it("keeps a guarded dialog open when the confirmation is cancelled", async () => {
		const onClose = vi.fn();
		renderWithProviders(
			<DialogShell opened={true} onClose={onClose} title="Editor" confirmCloseWhen={true}>
				<div>body</div>
			</DialogShell>,
		);

		fireEvent.click(screen.getByRole("button", { name: "close" }));

		await waitFor(() => {
			expect(screen.getByText("Discard unsaved changes?")).toBeTruthy();
		});

		fireEvent.click(screen.getByRole("button", { name: "Keep editing" }));

		await waitFor(() => {
			expect(screen.queryByText("Discard unsaved changes?")).toBeNull();
		});
		expect(onClose).not.toHaveBeenCalled();
	});
});
