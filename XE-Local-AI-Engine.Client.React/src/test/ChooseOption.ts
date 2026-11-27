import { fireEvent, screen, waitFor, within } from "@testing-library/react";

/** Opens the Mantine Select named `label` and returns its listbox, found through the combobox's `aria-controls`. */
export async function openOptions(label: string | RegExp): Promise<HTMLElement> {
	const select = await screen.findByRole("combobox", { name: label });
	fireEvent.click(select);
	return waitFor(() => {
		const element = document.getElementById(select.getAttribute("aria-controls") ?? "");
		if (element === null) {
			throw new Error("the dropdown is not open yet");
		}
		return element;
	});
}

/** Picks `option` in the Mantine Select named `label`. */
export async function chooseOption(label: string | RegExp, option: string): Promise<void> {
	const listbox = await openOptions(label);
	fireEvent.click(await within(listbox).findByRole("option", { name: option, hidden: true }));
}
