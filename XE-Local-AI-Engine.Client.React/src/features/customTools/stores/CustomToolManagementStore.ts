import { create } from "zustand";

// Transient UI state for the custom-tool management page: which editor (if any) is open. The target is the tool id, or
// "create" for the create form, or null when closed. Server state lives in TanStack Query; this store holds only
// ephemeral view state and the page resets it on unmount so navigating away and back never reopens a stale editor.
export type CustomToolEditorTarget = { mode: "create" } | { mode: "edit"; id: string } | null;

interface CustomToolManagementStore {
	editorTarget: CustomToolEditorTarget;
	actions: {
		openCreate: () => void;
		openEdit: (id: string) => void;
		closeEditor: () => void;
	};
}

export const useCustomToolManagementStore = create<CustomToolManagementStore>()((set) => ({
	editorTarget: null,
	actions: {
		openCreate: () => set({ editorTarget: { mode: "create" } }),
		openEdit: (id) => set({ editorTarget: { mode: "edit", id } }),
		closeEditor: () => set({ editorTarget: null }),
	},
}));
