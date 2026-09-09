import { createEditorTargetStore } from "@/core/ui/stores/EditorTargetStore";

// Transient UI state for the skill-library management page: which editor (if any) is open, keyed by skill id.
// Server state (the skills themselves) lives in TanStack Query; the page resets this store on unmount.
export const useSkillManagementStore = createEditorTargetStore();
