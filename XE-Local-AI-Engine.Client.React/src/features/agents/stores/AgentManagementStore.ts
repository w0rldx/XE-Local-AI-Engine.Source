import { createEditorTargetStore } from "@/core/ui/stores/EditorTargetStore";

// Transient UI state for the agent-management page: which editor (if any) is open, keyed by agent definition id.
// Server state (the definitions themselves) lives in TanStack Query.
export const useAgentManagementStore = createEditorTargetStore();
