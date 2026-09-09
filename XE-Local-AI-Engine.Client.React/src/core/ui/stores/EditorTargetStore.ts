import { create } from "zustand";

// The "which editor is open" UI state every management page needs: the entity being edited, the sentinel "create"
// for the create form, or null when the editor is closed. Server state (the entities themselves) lives in TanStack
// Query — a store built here holds only ephemeral view state, and a page that resets it on unmount never reopens a
// stale editor after navigating away and back (the unified-dialog "stuck editor" fix).
export type EditorTarget<TId = string> = { mode: "create" } | { mode: "edit"; id: TId } | null;

export interface EditorTargetState<TId = string> {
	editorTarget: EditorTarget<TId>;
	actions: {
		openCreate: () => void;
		openEdit: (id: TId) => void;
		closeEditor: () => void;
	};
}

// Builds one such store. Each feature keeps its own instance — the state is per-page, not global — so this is a
// factory rather than a shared store.
export function createEditorTargetStore<TId = string>() {
	return create<EditorTargetState<TId>>()((set) => ({
		editorTarget: null,
		actions: {
			openCreate: () => set({ editorTarget: { mode: "create" } }),
			openEdit: (id) => set({ editorTarget: { mode: "edit", id } }),
			closeEditor: () => set({ editorTarget: null }),
		},
	}));
}
