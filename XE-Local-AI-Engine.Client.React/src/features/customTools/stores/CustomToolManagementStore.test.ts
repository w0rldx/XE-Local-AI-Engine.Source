import { beforeEach, describe, expect, it } from "vitest";

import { useCustomToolManagementStore } from "@/features/customTools/stores/CustomToolManagementStore";

// Transient view state only — which editor is open. The page resets it on unmount precisely so navigating away and
// back cannot reopen a stale editor, so "closeEditor returns to null" is the invariant that keeps that reset honest.

describe("CustomToolManagementStore", () => {
	beforeEach(() => {
		useCustomToolManagementStore.getState().actions.closeEditor();
	});

	it("starts with no editor open", () => {
		expect(useCustomToolManagementStore.getState().editorTarget).toBeNull();
	});

	it("openCreate targets the create form without an id", () => {
		useCustomToolManagementStore.getState().actions.openCreate();

		expect(useCustomToolManagementStore.getState().editorTarget).toEqual({ mode: "create" });
	});

	it("openEdit targets the given tool id", () => {
		useCustomToolManagementStore.getState().actions.openEdit("tool-7");

		expect(useCustomToolManagementStore.getState().editorTarget).toEqual({ mode: "edit", id: "tool-7" });
	});

	it("openEdit replaces an open create target rather than stacking", () => {
		useCustomToolManagementStore.getState().actions.openCreate();
		useCustomToolManagementStore.getState().actions.openEdit("tool-7");

		expect(useCustomToolManagementStore.getState().editorTarget).toEqual({ mode: "edit", id: "tool-7" });
	});

	it("closeEditor clears the target", () => {
		useCustomToolManagementStore.getState().actions.openEdit("tool-7");
		useCustomToolManagementStore.getState().actions.closeEditor();

		expect(useCustomToolManagementStore.getState().editorTarget).toBeNull();
	});

	it("keeps the actions object referentially stable so selectors do not re-render", () => {
		const before = useCustomToolManagementStore.getState().actions;
		useCustomToolManagementStore.getState().actions.openCreate();

		expect(useCustomToolManagementStore.getState().actions).toBe(before);
	});
});
