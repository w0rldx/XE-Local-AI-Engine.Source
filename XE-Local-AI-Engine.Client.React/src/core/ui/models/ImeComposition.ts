import type { KeyboardEvent as ReactKeyboardEvent } from "react";

/**
 * True while an IME composition owns the key, so Enter confirms a conversion instead of submitting or moving focus.
 * `isComposing` alone is not enough: WebKit reports the keydown that ends a composition with `keyCode` 229 only.
 */
export function isImeComposing(event: KeyboardEvent | ReactKeyboardEvent): boolean {
	const native = "nativeEvent" in event ? event.nativeEvent : event;
	return native.isComposing || native.keyCode === 229;
}
