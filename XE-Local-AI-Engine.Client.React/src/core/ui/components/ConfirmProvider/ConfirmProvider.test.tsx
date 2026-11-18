// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { use } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import type { ConfirmContextType } from "@/core/ui/models/Confirm";
import { installJsdomEnvironmentMocks } from "@/test/MantineTestRender";
import { renderWithProviders } from "@/test/RenderWithProviders";

function AskButton() {
	const confirmContext = use(ConfirmContext);
	return (
		<button type="button" onClick={() => confirmContext?.confirm({ title: "Delete this chat?" })}>
			Ask
		</button>
	);
}

// Hands the test the provider's confirm, rendered under a real (non-zero) modal transition.
function renderWithSlowModal(): { readonly confirm: ConfirmContextType["confirm"] } {
	installJsdomEnvironmentMocks();
	const captured: { confirm?: ConfirmContextType["confirm"] } = {};
	function Capture() {
		captured.confirm = use(ConfirmContext)?.confirm;
		return null;
	}
	render(
		<MantineProvider
			env="default"
			theme={{ components: { Modal: { defaultProps: { transitionProps: { duration: 30, exitDuration: 30 } } } } }}
		>
			<ConfirmProvider>
				<Capture />
			</ConfirmProvider>
		</MantineProvider>,
	);
	if (captured.confirm === undefined) {
		throw new Error("ConfirmProvider gave no confirm");
	}
	return { confirm: captured.confirm };
}

describe("ConfirmProvider", () => {
	afterEach(() => {
		cleanup();
	});

	// A confirmation that announces only "dialog" leaves a screen-reader user guessing what they are about to confirm.
	it("names the confirmation dialog after the question it asks", async () => {
		renderWithProviders(
			<ConfirmProvider>
				<AskButton />
			</ConfirmProvider>,
		);

		fireEvent.click(screen.getByRole("button", { name: "Ask" }));

		expect(await screen.findByRole("dialog", { name: "Delete this chat?" })).toBeTruthy();
	});

	it("runs onClosed once, after the dialog's exit transition has unmounted it", async () => {
		const { confirm } = renderWithSlowModal();
		const dialogAtClose: (Element | null)[] = [];
		const onClosed = vi.fn(() => dialogAtClose.push(document.querySelector('[role="dialog"]')));

		act(() => {
			confirm({ title: "First", onClosed });
		});
		fireEvent.click(await screen.findByTestId("confirm-accept"));
		expect(screen.queryByRole("dialog")).not.toBeNull();
		expect(onClosed).not.toHaveBeenCalled();
		await waitFor(() => expect(onClosed).toHaveBeenCalledTimes(1));
		expect(dialogAtClose).toEqual([null]);

		// A later dialog without its own callback must not run the old one again.
		act(() => {
			confirm({ title: "Second" });
		});
		fireEvent.click(await screen.findByTestId("confirm-cancel"));
		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		expect(onClosed).toHaveBeenCalledTimes(1);
	});

	it("never runs the onClosed of a confirm a newer one replaced", async () => {
		const { confirm } = renderWithSlowModal();
		const superseded = vi.fn();
		const current = vi.fn();

		act(() => {
			confirm({ title: "Old", onClosed: superseded });
		});
		await screen.findByRole("dialog", { name: "Old" });
		act(() => {
			confirm({ title: "New", onClosed: current });
		});
		fireEvent.click(await screen.findByTestId("confirm-accept"));

		await waitFor(() => expect(current).toHaveBeenCalledTimes(1));
		expect(superseded).not.toHaveBeenCalled();
	});
});
