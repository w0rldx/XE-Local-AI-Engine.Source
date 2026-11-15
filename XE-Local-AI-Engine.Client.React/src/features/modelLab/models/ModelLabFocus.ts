import type { KeyboardEvent } from "react";

// The arrow keys, Home and End move between a group's buttons, so a long row needs no Tab press per button.
export function moveFocusAmongButtons(event: KeyboardEvent<HTMLElement>): void {
	const buttons = [...event.currentTarget.querySelectorAll("button")];
	const at = buttons.indexOf(document.activeElement as HTMLButtonElement);
	const next = { ArrowRight: at + 1, ArrowDown: at + 1, ArrowLeft: at - 1, ArrowUp: at - 1, Home: 0, End: buttons.length - 1 }[
		event.key
	];
	if (at !== -1 && next !== undefined && buttons[next]) {
		event.preventDefault();
		buttons[next].focus();
	}
}
