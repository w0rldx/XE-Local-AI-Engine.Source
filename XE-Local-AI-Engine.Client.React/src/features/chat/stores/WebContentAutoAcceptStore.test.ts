// @vitest-environment jsdom

import { renderHook } from "@testing-library/react";
import { act } from "react";
import { afterEach, describe, expect, it } from "vitest";

import {
	adoptWebContentAutoAcceptDraft,
	forgetWebContentAutoAccept,
	isWebContentAutoAccepted,
	useSetWebContentAutoAccept,
	useWebContentAutoAccept,
} from "@/features/chat/stores/WebContentAutoAcceptStore";

// Other modules (i18n) share localStorage, so only this store's keys are counted.
function storedChoices(): string[] {
	return Object.keys(localStorage).filter((key) => key.startsWith("xe-node-chat-web-auto:"));
}

// The empty id is the draft before its first send: its choice stays in memory until the created conversation adopts it.
describe("WebContentAutoAcceptStore draft slot", () => {
	afterEach(() => {
		forgetWebContentAutoAccept("");
		forgetWebContentAutoAccept("conv-adopted");
		localStorage.clear();
	});

	it("keeps a draft's choice in memory and moves it onto the conversation that adopts it", () => {
		const setter = renderHook(() => useSetWebContentAutoAccept());
		act(() => setter.result.current("", true));

		const draft = renderHook(() => useWebContentAutoAccept(""));
		expect(draft.result.current).toBe(true);
		expect(storedChoices()).toEqual([]);
		// A send always targets a real id, so the draft itself never reads as accepted.
		expect(isWebContentAutoAccepted("")).toBe(false);

		act(() => adoptWebContentAutoAcceptDraft("conv-adopted"));

		expect(isWebContentAutoAccepted("conv-adopted")).toBe(true);
		expect(draft.result.current).toBe(false);
		expect(storedChoices()).toEqual(["xe-node-chat-web-auto:conv-adopted"]);
		expect(localStorage.getItem("xe-node-chat-web-auto:conv-adopted")).toBe("true");
	});

	it("adopts nothing when the draft made no choice", () => {
		adoptWebContentAutoAcceptDraft("conv-adopted");

		expect(isWebContentAutoAccepted("conv-adopted")).toBe(false);
		expect(storedChoices()).toEqual([]);
	});
});
