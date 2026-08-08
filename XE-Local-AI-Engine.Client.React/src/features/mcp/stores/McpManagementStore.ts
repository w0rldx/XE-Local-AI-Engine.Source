import { create } from "zustand";

// Transient UI state for the MCP-server management page: which editor (if any) is open. The editing target is
// the MCP server id, or the sentinel "create" for the create form, or null when the editor is closed. Server
// state (the registrations themselves) lives in TanStack Query — this store holds only ephemeral view state.
export type McpEditorTarget = { mode: "create" } | { mode: "edit"; id: string } | null;

interface McpManagementStore {
	editorTarget: McpEditorTarget;
	actions: {
		openCreate: () => void;
		openEdit: (id: string) => void;
		closeEditor: () => void;
	};
}

export const useMcpManagementStore = create<McpManagementStore>()((set) => ({
	editorTarget: null,
	actions: {
		openCreate: () => set({ editorTarget: { mode: "create" } }),
		openEdit: (id) => set({ editorTarget: { mode: "edit", id } }),
		closeEditor: () => set({ editorTarget: null }),
	},
}));
