import { create } from "zustand";

// Transient UI state for the Preview management page: which workflow (if any) is open on the canvas. The editing
// target is a saved workflow id, the sentinel "new" for a fresh unsaved graph, or null when the workflow list is
// shown. Server state (the workflows themselves + live run output) lives in TanStack Query / PreviewRunStore —
// this store holds only ephemeral view state.
export type PreviewCanvasTarget = { mode: "new" } | { mode: "open"; id: string } | null;

interface PreviewManagementStore {
	canvasTarget: PreviewCanvasTarget;
	actions: {
		openNew: () => void;
		openWorkflow: (id: string) => void;
		closeCanvas: () => void;
	};
}

export const usePreviewManagementStore = create<PreviewManagementStore>()((set) => ({
	canvasTarget: null,
	actions: {
		openNew: () => set({ canvasTarget: { mode: "new" } }),
		openWorkflow: (id) => set({ canvasTarget: { mode: "open", id } }),
		closeCanvas: () => set({ canvasTarget: null }),
	},
}));
