import { useQuery } from "@tanstack/react-query";

import { getNodeSettingsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { CUSTOM_TOOL_TIMEOUT_MAX_FALLBACK } from "@/features/customTools/models/CustomToolModels";

// The node's custom-tool timeout ceiling (node setting customToolMaxTimeoutSeconds), read from the shared node-settings
// query so the form bounds the timeout the way the backend will. The fallback applies until the settings load.
export function useCustomToolMaxTimeoutSeconds(): number {
	const { data } = useQuery(getNodeSettingsOptions());
	return data?.customToolMaxTimeoutSeconds ?? CUSTOM_TOOL_TIMEOUT_MAX_FALLBACK;
}
