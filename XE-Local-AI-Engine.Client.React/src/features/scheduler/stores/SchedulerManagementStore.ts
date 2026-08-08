import { create } from "zustand";

// Transient UI state for the scheduler management page: which editor (if any) is open and which run's detail is
// selected. The editing target is the scheduled-job id, or the sentinel "create" for the create form, or null
// when the editor is closed. Server state (the jobs/runs themselves) lives in TanStack Query — this store holds
// only ephemeral view state.
export type SchedulerEditorTarget = { mode: "create" } | { mode: "edit"; id: string } | null;

interface SchedulerManagementStore {
	editorTarget: SchedulerEditorTarget;
	// The run whose redacted detail panel is open, or null when none is selected.
	selectedRunId: string | null;
	actions: {
		openCreate: () => void;
		openEdit: (id: string) => void;
		closeEditor: () => void;
		selectRun: (runId: string | null) => void;
	};
}

export const useSchedulerManagementStore = create<SchedulerManagementStore>()((set) => ({
	editorTarget: null,
	selectedRunId: null,
	actions: {
		openCreate: () => set({ editorTarget: { mode: "create" } }),
		openEdit: (id) => set({ editorTarget: { mode: "edit", id } }),
		closeEditor: () => set({ editorTarget: null }),
		selectRun: (runId) => set({ selectedRunId: runId }),
	},
}));
