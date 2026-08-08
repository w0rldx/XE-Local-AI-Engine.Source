import { create } from "zustand";

// Transient UI state for the skill-library management page: which editor (if any) is open. The editing target is
// the skill id, or the sentinel "create" for the create form, or null when the editor is closed. Server state (the
// skills themselves) lives in TanStack Query — this store holds only ephemeral view state, and the page resets it
// on unmount so navigating away and back never reopens a stale editor (the unified-dialog "stuck editor" fix).
export type SkillEditorTarget = { mode: "create" } | { mode: "edit"; id: string } | null;

interface SkillManagementStore {
	editorTarget: SkillEditorTarget;
	actions: {
		openCreate: () => void;
		openEdit: (id: string) => void;
		closeEditor: () => void;
	};
}

export const useSkillManagementStore = create<SkillManagementStore>()((set) => ({
	editorTarget: null,
	actions: {
		openCreate: () => set({ editorTarget: { mode: "create" } }),
		openEdit: (id) => set({ editorTarget: { mode: "edit", id } }),
		closeEditor: () => set({ editorTarget: null }),
	},
}));
