// Tool-capability policy for the agents form. The capable-model list comes from the backend
// (AgentHomeOptions.ToolCapableModels). Policy:
//   - empty list  -> capability source unavailable -> do NOT enforce (return true so the page keeps working)
//   - non-empty   -> a model is tool-capable iff its name is in the list; a null modelProfile (node default)
//                    is treated as capable (the node default is expected to be tool-capable).
// When this returns false the page disables tool selection and shows a warning (no silent no-op).
export function isModelToolCapable(modelProfile: string | null, toolCapableModels: readonly string[]): boolean {
	if (toolCapableModels.length === 0) {
		return true;
	}

	if (modelProfile === null || modelProfile.trim().length === 0) {
		return true;
	}

	return toolCapableModels.includes(modelProfile);
}
