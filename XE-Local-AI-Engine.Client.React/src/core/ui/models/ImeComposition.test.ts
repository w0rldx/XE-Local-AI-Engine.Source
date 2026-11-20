// @vitest-environment jsdom

import type { KeyboardEvent as ReactKeyboardEvent } from "react";
import { describe, expect, it } from "vitest";

import { isImeComposing } from "@/core/ui/models/ImeComposition";

describe("isImeComposing", () => {
	it("is true for an isComposing keydown and for WebKit's keyCode 229, native or wrapped by React", () => {
		const composing = new KeyboardEvent("keydown", { key: "Enter", isComposing: true });
		const webkit = new KeyboardEvent("keydown", { key: "Enter", keyCode: 229 });

		expect(isImeComposing(composing)).toBe(true);
		expect(isImeComposing(webkit)).toBe(true);
		expect(isImeComposing({ nativeEvent: webkit } as ReactKeyboardEvent)).toBe(true);
	});

	it("is false for a plain Enter", () => {
		const plain = new KeyboardEvent("keydown", { key: "Enter", keyCode: 13 });

		expect(isImeComposing(plain)).toBe(false);
		expect(isImeComposing({ nativeEvent: plain } as ReactKeyboardEvent)).toBe(false);
	});
});
