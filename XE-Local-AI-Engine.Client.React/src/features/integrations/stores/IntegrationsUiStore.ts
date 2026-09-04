import { create } from "zustand";

// Transient UI state for the External Integrations pages: which editor or dialog is open. Server state lives in
// TanStack Query. The revealed plaintext API key deliberately does NOT live here — it is held in component state
// inside IntegrationKeysPage so unmounting or navigating away drops it, which is the honest lifetime for a value the
// node can never supply again.
export type IntegrationEditorTarget = { mode: "create" } | { mode: "edit"; id: string } | null;

interface IntegrationsUiStore {
	editorTarget: IntegrationEditorTarget;
	/** True while the generate-key dialog is open. */
	keyDialogOpen: boolean;
	/** The execution whose detail dialog is open, or null. */
	selectedExecutionId: string | null;
	/** The session whose detail dialog is open, or null. */
	selectedSessionId: string | null;
	actions: {
		openCreate: () => void;
		openEdit: (id: string) => void;
		closeEditor: () => void;
		openKeyDialog: () => void;
		closeKeyDialog: () => void;
		selectExecution: (id: string | null) => void;
		selectSession: (id: string | null) => void;
	};
}

export const useIntegrationsUiStore = create<IntegrationsUiStore>()((set) => ({
	editorTarget: null,
	keyDialogOpen: false,
	selectedExecutionId: null,
	selectedSessionId: null,
	actions: {
		openCreate: () => set({ editorTarget: { mode: "create" } }),
		openEdit: (id) => set({ editorTarget: { mode: "edit", id } }),
		closeEditor: () => set({ editorTarget: null }),
		openKeyDialog: () => set({ keyDialogOpen: true }),
		closeKeyDialog: () => set({ keyDialogOpen: false }),
		selectExecution: (id) => set({ selectedExecutionId: id }),
		selectSession: (id) => set({ selectedSessionId: id }),
	},
}));
