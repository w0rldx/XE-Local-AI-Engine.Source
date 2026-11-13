// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { use } from "react";
import { afterEach, describe, expect, it } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import { renderWithProviders } from "@/test/RenderWithProviders";

function AskButton() {
	const confirmContext = use(ConfirmContext);
	return (
		<button type="button" onClick={() => confirmContext?.confirm({ title: "Delete this chat?" })}>
			Ask
		</button>
	);
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
});
