import { createEditorTargetStore } from "@/core/ui/stores/EditorTargetStore";

// Transient UI state for the MCP-server management page: which editor (if any) is open, keyed by MCP server id.
// Server state (the registrations themselves) lives in TanStack Query.
export const useMcpManagementStore = createEditorTargetStore();
