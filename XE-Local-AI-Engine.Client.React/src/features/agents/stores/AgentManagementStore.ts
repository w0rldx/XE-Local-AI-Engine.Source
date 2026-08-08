import { create } from "zustand";

// Transient UI state for the agent-management page: which editor (if any) is open. The editing target is the
// agent definition id, or the sentinel "new" for the create form, or null when the editor is closed. Server
// state (the definitions themselves) lives in TanStack Query — this store holds only ephemeral view state.
export type AgentEditorTarget = { mode: "create" } | { mode: "edit"; id: string } | null;

interface AgentManagementStore {
	editorTarget: AgentEditorTarget;
	actions: {
		openCreate: () => void;
		openEdit: (id: string) => void;
		closeEditor: () => void;
	};
}

export const useAgentManagementStore = create<AgentManagementStore>()((set) => ({
	editorTarget: null,
	actions: {
		openCreate: () => set({ editorTarget: { mode: "create" } }),
		openEdit: (id) => set({ editorTarget: { mode: "edit", id } }),
		closeEditor: () => set({ editorTarget: null }),
	},
}));
