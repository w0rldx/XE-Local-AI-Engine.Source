// @vitest-environment jsdom

import "@/i18n";

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("@/features/about/components/AboutDialog/AboutDialog", () => ({
	AboutDialog: ({ opened, onClose }: { opened: boolean; onClose: () => void }) => (
		<div data-testid="about-dialog-lifecycle" data-opened={String(opened)}>
			<button type="button" onClick={onClose}>Close mocked About</button>
		</div>
	),
}));

import { AboutDialogButton } from "./AboutDialogButton";

describe("AboutDialogButton", () => {
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
		vi.clearAllMocks();
	});

	it("lazy-loads once and keeps the update lifecycle mounted after close", async () => {
		render(<MantineProvider><AboutDialogButton /></MantineProvider>);
		expect(screen.queryByTestId("about-dialog-lifecycle")).toBeNull();

		fireEvent.click(screen.getByRole("button", { name: "About" }));
		await waitFor(() => expect(screen.getByTestId("about-dialog-lifecycle").dataset["opened"]).toBe("true"));
		fireEvent.click(screen.getByRole("button", { name: "Close mocked About" }));

		await waitFor(() => expect(screen.getByTestId("about-dialog-lifecycle").dataset["opened"]).toBe("false"));
	});
});
