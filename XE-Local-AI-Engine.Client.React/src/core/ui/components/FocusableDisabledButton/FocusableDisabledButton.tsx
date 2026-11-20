import { Button, type ButtonProps, Loader } from "@mantine/core";
import type { ComponentPropsWithoutRef } from "react";

type FocusableDisabledButtonProps = Omit<ButtonProps, "disabled" | "loading"> &
	Omit<ComponentPropsWithoutRef<"button">, keyof ButtonProps | "disabled"> & {
		/** The action is unavailable; the button stays focusable and ignores clicks, Enter and Space. */
		readonly blocked?: boolean;
		/** The action is in flight: blocked as above, with a loader in place of the left section. */
		readonly pending?: boolean;
		/** Pixel size of the pending loader; match the left-section icon. */
		readonly loaderSize?: number;
	};

// Mantine renders `disabled || loading` as native `disabled`, and a native disabled button cannot hold focus: a push
// that disables the focused button drops focus to the body. This one keeps focus and the tab stop, uses Mantine's
// `data-disabled` style (Mantine excludes `[data-loading]` from it, so `loading` is never set) and swallows the click,
// which is also what Enter and Space dispatch on a button: cancelled, so a submit button or a clickable parent never acts.
export function FocusableDisabledButton({
	blocked = false,
	pending = false,
	loaderSize = 14,
	leftSection,
	onClick,
	...props
}: FocusableDisabledButtonProps) {
	const inert = blocked || pending;
	return (
		<Button
			{...props}
			aria-disabled={inert || undefined}
			aria-busy={pending || undefined}
			data-disabled={inert || undefined}
			leftSection={pending ? <Loader size={loaderSize} color="currentColor" /> : leftSection}
			onClick={
				inert
					? (event) => {
							event.preventDefault();
							event.stopPropagation();
						}
					: onClick
			}
		/>
	);
}
