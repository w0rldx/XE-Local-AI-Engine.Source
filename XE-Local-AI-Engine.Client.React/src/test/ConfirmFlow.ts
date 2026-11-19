import { fireEvent, screen, waitFor } from "@testing-library/react";
import { expect } from "vitest";

/**
 * Activates `opener` with the focus on it, as a keyboard user would, and returns the `ConfirmProvider` dialog that it
 * opened, found by the question it asks.
 */
export async function openConfirm(opener: HTMLElement, title: string): Promise<HTMLElement> {
	opener.focus();
	fireEvent.click(opener);
	return screen.findByRole("dialog", { name: title });
}

/** Answers the open confirmation with Cancel and waits until the focus is back on the control that opened it. */
export async function cancelConfirm(opener: HTMLElement): Promise<void> {
	fireEvent.click(screen.getByTestId("confirm-cancel"));
	await waitFor(() => expect(document.activeElement).toBe(opener));
}

/** Answers the open confirmation with its confirming button. */
export function acceptConfirm(): void {
	fireEvent.click(screen.getByTestId("confirm-accept"));
}
