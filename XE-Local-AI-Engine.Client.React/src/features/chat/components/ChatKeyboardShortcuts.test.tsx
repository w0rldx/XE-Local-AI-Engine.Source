// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import i18next from "i18next";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ChatKeyboardShortcuts } from "@/features/chat/components/ChatKeyboardShortcuts";
import de from "@/locales/de.json";
import { testMantineTheme } from "@/test/MantineTestRender";

function renderWithProviders(ui: ReactElement) {
	return render(
		<MantineProvider env="test" theme={testMantineTheme}>
			{ui}
		</MantineProvider>,
	);
}

describe("ChatKeyboardShortcuts", () => {
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

	it("stops the running answer on Esc", () => {
		const onStop = vi.fn();
		renderWithProviders(<ChatKeyboardShortcuts onStop={onStop} />);

		fireEvent.keyDown(document.body, { key: "Escape" });

		expect(onStop).toHaveBeenCalledTimes(1);
	});

	it("leaves Esc alone during an IME composition, after another handler took it, or inside a popup", () => {
		const onStop = vi.fn();
		renderWithProviders(
			<>
				<ChatKeyboardShortcuts onStop={onStop} />
				<div role="menu">
					<button type="button">Menu item</button>
				</div>
			</>,
		);

		fireEvent.keyDown(document.body, { key: "Escape", isComposing: true });
		const taken = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
		taken.preventDefault();
		document.body.dispatchEvent(taken);
		fireEvent.keyDown(screen.getByRole("button", { name: "Menu item" }), { key: "Escape" });

		expect(onStop).not.toHaveBeenCalled();
	});

	it("leaves Esc to an expanded combobox or popover target that keeps the focus", () => {
		const onStop = vi.fn();
		renderWithProviders(
			<>
				<ChatKeyboardShortcuts onStop={onStop} />
				{/* Closes itself on its own keydown, before the document sees the key, as a Mantine Select does once React
				    commits its close. */}
				<input
					role="combobox"
					aria-controls="model-options"
					aria-label="Model"
					aria-expanded="true"
					onKeyDown={(event) => event.currentTarget.setAttribute("aria-expanded", "false")}
				/>
				<button type="button" data-mantine-stop-propagation="true">
					Options
				</button>
			</>,
		);

		fireEvent.keyDown(screen.getByLabelText("Model"), { key: "Escape" });
		fireEvent.keyDown(screen.getByRole("button", { name: "Options" }), { key: "Escape" });

		expect(onStop).not.toHaveBeenCalled();
	});

	it("names the modifier keys in the interface language", async () => {
		i18next.addResourceBundle("de", "translation", de, true, true);
		await i18next.changeLanguage("de");
		try {
			renderWithProviders(<ChatKeyboardShortcuts />);

			fireEvent.keyDown(document.body, { key: "/", ctrlKey: true });

			const keys = Array.from(
				(await screen.findByTestId("chat-shortcuts-dialog")).querySelectorAll("kbd"),
				(kbd) => kbd.textContent,
			);
			expect(keys).toEqual(["Eingabe", "Umschalt", "Eingabe", "Esc", "Strg", "Umschalt", "O", "Strg", "/", "Strg", "K"]);
		} finally {
			await i18next.changeLanguage("en");
		}
	});

	it("starts a new chat on Ctrl+Shift+O and Cmd+Shift+O", () => {
		const onNewChat = vi.fn();
		renderWithProviders(<ChatKeyboardShortcuts onNewChat={onNewChat} />);

		fireEvent.keyDown(document.body, { key: "O", ctrlKey: true, shiftKey: true });
		fireEvent.keyDown(document.body, { key: "O", metaKey: true, shiftKey: true });
		fireEvent.keyDown(document.body, { key: "o", ctrlKey: true });

		expect(onNewChat).toHaveBeenCalledTimes(2);
	});

	it("opens the shortcut list on Ctrl+/ and closes it on Esc without stopping the answer", async () => {
		const onStop = vi.fn();
		renderWithProviders(<ChatKeyboardShortcuts onStop={onStop} />);

		fireEvent.keyDown(document.body, { key: "/", ctrlKey: true });

		const dialog = await screen.findByTestId("chat-shortcuts-dialog");
		expect(dialog.textContent).toContain("Keyboard shortcuts");
		expect(dialog.textContent).toContain("Stop the running answer");
		expect(dialog.textContent).toContain("Start a new chat");
		fireEvent.keyDown(document.body, { key: "Escape" });
		expect(onStop).not.toHaveBeenCalled();
	});
});
