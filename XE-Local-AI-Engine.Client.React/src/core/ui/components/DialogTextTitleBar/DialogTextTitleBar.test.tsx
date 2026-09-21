// @vitest-environment jsdom

import "@/i18n";

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { DialogTextTitleBar } from "@/core/ui/components/DialogTextTitleBar/DialogTextTitleBar";
import { testMantineTheme } from "@/test/MantineTestRender";

function renderWithProviders(ui: ReactElement) {
	return render(
		<MantineProvider env="test" theme={testMantineTheme}>
			{ui}
		</MantineProvider>,
	);
}

describe("DialogTextTitleBar", () => {
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

	// The close button's accessible name is the shipped "Close" string, not the untranslated literal "close" it
	// carried before: a German session announced an English word, and it disagreed with its own tooltip.
	it("renders the title and a close button named from the bundle", () => {
		const handleClose = vi.fn();
		renderWithProviders(<DialogTextTitleBar title="My dialog" handleClose={handleClose} />);

		expect(screen.getByText("My dialog")).toBeTruthy();
		expect(screen.queryByRole("button", { name: "close" })).toBeNull();

		fireEvent.click(screen.getByRole("button", { name: "Close" }));
		expect(handleClose).toHaveBeenCalledTimes(1);
	});

	it("does not render the fullscreen toggle by default", () => {
		renderWithProviders(<DialogTextTitleBar title="My dialog" handleClose={vi.fn()} />);

		expect(screen.queryByRole("button", { name: "Fullscreen" })).toBeNull();
		expect(screen.queryByRole("button", { name: "Exit fullscreen" })).toBeNull();
	});

	it("renders the fullscreen toggle and invokes the callback when enabled", () => {
		const onToggleFullScreen = vi.fn();
		renderWithProviders(
			<DialogTextTitleBar
				title="My dialog"
				handleClose={vi.fn()}
				showFullScreenToggle={true}
				isFullScreen={false}
				onToggleFullScreen={onToggleFullScreen}
			/>,
		);

		const toggle = screen.getByRole("button", { name: "Fullscreen" });
		fireEvent.click(toggle);
		expect(onToggleFullScreen).toHaveBeenCalledTimes(1);
	});

	it("shows the exit-fullscreen affordance when already fullscreen", () => {
		renderWithProviders(
			<DialogTextTitleBar
				title="My dialog"
				handleClose={vi.fn()}
				showFullScreenToggle={true}
				isFullScreen={true}
				onToggleFullScreen={vi.fn()}
			/>,
		);

		expect(screen.getByRole("button", { name: "Exit fullscreen" })).toBeTruthy();
		expect(screen.queryByRole("button", { name: "Fullscreen" })).toBeNull();
	});
});
