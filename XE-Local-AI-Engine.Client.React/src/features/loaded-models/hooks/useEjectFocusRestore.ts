import { type FocusEvent, useEffect, useRef } from "react";

/**
 * Where focus goes when an Eject's dialogs close or its row goes away: back to the Eject, else to the heading, and never
 * onto another row's Eject, where a repeated Enter would unload a second model. `rowKeys` changes when a row is removed.
 * Spread `cardFocusHandlers` on the element holding the rows; `restoreFocusTo` makes the `restoreFocus` for one click.
 */
export function useEjectFocusRestore(rowKeys: string) {
	const headingRef = useRef<HTMLHeadingElement>(null);
	// The control inside the card that last took focus; cleared once focus moves to another element or to the body (a
	// click on blank page space). A removed element can also blur with no related target, so that case is told apart a
	// frame later: a removed element is no longer connected and stays remembered.
	const focusedRef = useRef<HTMLElement | null>(null);
	const ejectClicksRef = useRef(0);

	// When the focused row goes away (an eject, a push), the browser drops focus to the body; hand it to the heading
	// rather than to another row's Eject, where a repeated Enter would unload a second model.
	// biome-ignore lint/correctness/useExhaustiveDependencies: rowKeys is a trigger (a row removed under focus), not a value the body reads.
	useEffect(() => {
		const focused = focusedRef.current;
		const active = document.activeElement;
		if (focused !== null && !focused.isConnected && (active === null || active === document.body)) {
			headingRef.current?.focus();
		}
	}, [rowKeys]);

	const cardFocusHandlers = {
		onFocus: (event: FocusEvent<HTMLElement>) => {
			focusedRef.current = event.target;
		},
		onBlur: (event: FocusEvent<HTMLElement>) => {
			const blurred = event.target;
			if (event.relatedTarget !== null) {
				focusedRef.current = null;
				return;
			}
			requestAnimationFrame(() => {
				if (blurred.isConnected && focusedRef.current === blurred) {
					focusedRef.current = null;
				}
			});
		},
	};

	// Each confirmation runs this once it has closed: only lost focus comes back, and only to the newest flow. A dialog
	// hands focus back to its opener, which is detached when the row went while it was open; then the heading takes it.
	// Nothing restores when the request ends, so a user who moved focus away meanwhile keeps it there; a row removed
	// later is the rowKeys effect's.
	const restoreFocusTo = (eject: HTMLElement) => {
		ejectClicksRef.current += 1;
		const click = ejectClicksRef.current;
		return () => {
			const active = document.activeElement;
			if (click === ejectClicksRef.current && (active === null || active === document.body)) {
				(eject.isConnected ? eject : headingRef.current)?.focus();
			}
		};
	};

	return { headingRef, cardFocusHandlers, restoreFocusTo };
}
