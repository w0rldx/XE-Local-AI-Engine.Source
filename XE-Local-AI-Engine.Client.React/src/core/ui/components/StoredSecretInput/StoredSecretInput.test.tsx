// @vitest-environment jsdom

// The keep/clear mechanic for a stored secret, asserted once for both editors that share it. The failure it guards is
// silent in every other layer: "keep" and "clear" reach the server as a sentinel and an empty string, so an emptied
// box used to destroy a stored password with nothing on screen saying it would.

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { StoredSecretInput } from "@/core/ui/components/StoredSecretInput/StoredSecretInput";
import { renderWithProviders } from "@/test/RenderWithProviders";

const sentinel = "__stored__";

// The editors own the value, so the test does too: the last reported value is what a save would send.
function Host({ initial }: { readonly initial: string }) {
	const [value, setValue] = useState(initial);
	// What the parent knows from the server, not from the live value: exactly what the editors pass.
	const stored = initial === sentinel;
	return (
		<>
			<StoredSecretInput
				sentinel={sentinel}
				value={value}
				onChange={setValue}
				stored={stored}
				storedPlaceholder="stored — leave empty to keep"
				data-testid="secret"
			/>
			<output data-testid="submitted">{value}</output>
		</>
	);
}

// A node the parent re-seeds for a DIFFERENT field without remounting it: the instance behind the detail page
// changes while the same form stays mounted, and a secret row keeps its position while its subject does not. The
// node must follow what the parent says about the NEW field and carry nothing over from the old one.
function ReseedHost() {
	const [field, setField] = useState("secret-a");
	const [stored, setStored] = useState(true);
	const [value, setValue] = useState(sentinel);
	return (
		<>
			<StoredSecretInput
				sentinel={sentinel}
				value={value}
				onChange={setValue}
				stored={stored}
				storedPlaceholder="stored — leave empty to keep"
				data-testid={field}
			/>
			<button
				type="button"
				data-testid="reseed"
				onClick={() => {
					setField("secret-b");
					setStored(false);
					setValue("");
				}}
			>
				reseed
			</button>
		</>
	);
}

function renderHost(initial: string) {
	renderWithProviders(<Host initial={initial} />);
	const input = screen.getByTestId("secret") as HTMLInputElement;
	const submitted = () => screen.getByTestId("submitted").textContent;
	return { input, submitted };
}

describe("StoredSecretInput", () => {
	afterEach(cleanup);

	it("renders a stored secret empty and keeps it when nothing is touched", () => {
		const { input, submitted } = renderHost(sentinel);

		expect(input.value).toBe("");
		expect(input.placeholder).toBe("stored — leave empty to keep");
		expect(submitted()).toBe(sentinel);
		expect(screen.queryByTestId("secret-cleared")).toBeNull();
	});

	it("still keeps the stored secret after the box is typed into and emptied again", () => {
		const { input, submitted } = renderHost(sentinel);

		fireEvent.change(input, { target: { value: "hunter2" } });
		expect(submitted()).toBe("hunter2");

		fireEvent.change(input, { target: { value: "" } });

		// The accidental clear: emptying the box reports the sentinel, never the empty string.
		expect(submitted()).toBe(sentinel);
		expect(screen.queryByTestId("secret-cleared")).toBeNull();
	});

	it("reports an empty value and says so once the clear action is used", () => {
		const { submitted } = renderHost(sentinel);

		fireEvent.click(screen.getByTestId("secret-clear"));

		expect(submitted()).toBe("");
		expect(screen.getByTestId("secret-cleared").textContent).toBe("This value will be removed when you save.");
	});

	it("puts the stored secret back when the clear is undone", () => {
		const { submitted } = renderHost(sentinel);

		fireEvent.click(screen.getByTestId("secret-clear"));
		fireEvent.click(screen.getByTestId("secret-undo"));

		expect(submitted()).toBe(sentinel);
		expect(screen.queryByTestId("secret-cleared")).toBeNull();
		expect(screen.getByTestId("secret-clear")).toBeDefined();
	});

	it("offers no clear action for a field that has no stored secret", () => {
		const { input, submitted } = renderHost("");

		fireEvent.change(input, { target: { value: "typed" } });
		fireEvent.change(input, { target: { value: "" } });

		// Nothing is stored, so an emptied box is an empty value and there is nothing to clear or keep.
		expect(submitted()).toBe("");
		expect(screen.queryByTestId("secret-clear")).toBeNull();
	});

	it("offers no clear action after the node is re-seeded for a field with no stored secret", () => {
		renderWithProviders(<ReseedHost />);

		expect(screen.getByTestId("secret-a-clear")).toBeDefined();

		fireEvent.click(screen.getByTestId("reseed"));

		// Nothing about the old field survives: the node reads only what the parent tells it about the new one.
		expect(screen.queryByTestId("secret-b-clear")).toBeNull();
		expect(screen.queryByTestId("secret-a-clear")).toBeNull();
		// A leaked latch reads the empty value as a PENDING CLEAR and offers to undo one that was never made.
		expect(screen.queryByTestId("secret-b-undo")).toBeNull();
		expect(screen.queryByTestId("secret-b-cleared")).toBeNull();
	});

	it("masks the typed value only when asked to", () => {
		renderWithProviders(
			<>
				<StoredSecretInput
					sentinel={sentinel}
					stored={false}
					value="typed"
					onChange={vi.fn()}
					storedPlaceholder="s"
					data-testid="plain"
				/>
				<StoredSecretInput
					sentinel={sentinel}
					stored={false}
					value="typed"
					onChange={vi.fn()}
					masked={true}
					storedPlaceholder="s"
					data-testid="masked"
				/>
			</>,
		);

		expect((screen.getByTestId("plain") as HTMLInputElement).type).toBe("text");
		expect((screen.getByTestId("masked") as HTMLInputElement).type).toBe("password");
	});
});
