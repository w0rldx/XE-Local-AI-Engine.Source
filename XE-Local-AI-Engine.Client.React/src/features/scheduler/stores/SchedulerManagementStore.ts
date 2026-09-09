import { create } from "zustand";

import type { EditorTarget } from "@/core/ui/stores/EditorTargetStore";

// Transient UI state for the scheduler management page: which editor (if any) is open, keyed by scheduled-job id,
// and which run's redacted detail panel is selected. Server state (the jobs/runs themselves) lives in TanStack
// Query. This is the one management store that is not `createEditorTargetStore()`: the run selection is a second,
// unrelated concern that the page and its tests read from the same hook, and threading it through the factory costs
// more indirection than the three action bodies below are worth. The target type is still the shared one.
interface SchedulerManagementStore {
	editorTarget: EditorTarget;
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
