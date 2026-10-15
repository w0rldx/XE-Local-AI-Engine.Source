import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";

import { listGraphWorkflowDefinitionsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { IntegrationGraphOption } from "@/features/integrations/models/IntegrationModels";

// Feature-local graph picker data, from the GENERATED sdk for the same reason as useIntegrationAgentOptions: a
// features/integrations → features/graphWorkflows import fails `pnpm depcruise`. A node with graph workflows off
// answers an error here and the picker stays empty; the backend refuses a graph target then anyway.
export function useIntegrationGraphOptions(): readonly IntegrationGraphOption[] {
	const definitionsQuery = useQuery({ ...withResponseValidation(listGraphWorkflowDefinitionsOptions()) });
	return useMemo(
		() =>
			(definitionsQuery.data?.definitions ?? [])
				.map((definition) => ({ id: definition.id, name: definition.name }))
				.sort((left, right) => left.name.localeCompare(right.name)),
		[definitionsQuery.data],
	);
}
