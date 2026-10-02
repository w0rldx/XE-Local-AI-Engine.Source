import { useQuery } from "@tanstack/react-query";

import { getToolCapableModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

// The EFFECTIVE tool-capable list (stored, else the appsettings seed). The node-settings response carries only the
// stored list, so the client mirror of "AgentHome needs a tool-capable model" reads this to avoid refusing a save the
// server would accept. Undefined while loading or on a failed read; callers treat that as "satisfied".
export function useEffectiveToolCapableModels(): readonly string[] | undefined {
	return useQuery(withResponseValidation(getToolCapableModelsOptions())).data?.models;
}
