import { createEditorTargetStore } from "@/core/ui/stores/EditorTargetStore";

// Transient UI state for the custom-tool management page: which editor (if any) is open, keyed by tool id.
// Server state (the tools themselves) lives in TanStack Query; the page resets this store on unmount.
export const useCustomToolManagementStore = createEditorTargetStore();
