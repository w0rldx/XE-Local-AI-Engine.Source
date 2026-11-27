import { type KeyboardEvent, useState } from "react";

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

// A button row's one Tab stop, by position: it follows focus, so Tab leaves from where the arrows went, and a new
// selection brings it back. Falls back to the selected button (-1: none in this row), then the first.
export function useRovingTabStop(
	selected: number,
	count: number,
): { tabIndexFor: (index: number) => 0 | -1; onFocusAt: (index: number) => void } {
	const [focused, setFocused] = useState(selected);
	const [selectedSeen, setSelectedSeen] = useState(selected);
	if (selected !== selectedSeen) {
		setSelectedSeen(selected);
		setFocused(selected);
	}
	const inRow = (index: number) => index >= 0 && index < count;
	const stop = inRow(focused) ? focused : inRow(selected) ? selected : 0;
	return { tabIndexFor: (index) => (index === stop ? 0 : -1), onFocusAt: setFocused };
}
