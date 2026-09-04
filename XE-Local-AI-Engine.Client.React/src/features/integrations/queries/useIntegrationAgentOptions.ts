import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";

import { getToolCatalogOptions, listAgentDefinitionsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { IntegrationAgentOption, IntegrationToolFacts } from "@/features/integrations/models/IntegrationModels";

// Feature-local agent picker data. This calls the GENERATED sdk directly rather than reusing features/agents or
// features/tools: `no-cross-feature` is baseline-fingerprinted per edge, so a features/integrations → features/*
// import fails `pnpm depcruise`.
//
// ONE read answers BOTH rules the trigger editor enforces — the approval banner and the CallerManaged preflight —
// so the two can never disagree about what the selected agent resolves.

interface IntegrationAgentOptionsResult {
	readonly options: readonly IntegrationAgentOption[];
	readonly toolsByName: ReadonlyMap<string, IntegrationToolFacts>;
	readonly isLoading: boolean;
	/** True when a read failed. An empty `toolsByName` then means "unknown", not "no tools", and the UI must say so. */
	readonly isError: boolean;
}

export function useIntegrationAgentOptions(): IntegrationAgentOptionsResult {
	const agentsQuery = useQuery({ ...withResponseValidation(listAgentDefinitionsOptions()) });
	const catalogQuery = useQuery({ ...withResponseValidation(getToolCatalogOptions()) });

	const options = useMemo<IntegrationAgentOption[]>(
		() =>
			(agentsQuery.data?.items ?? [])
				.map((agent) => ({
					id: agent.id,
					name: agent.name,
					description: agent.description ?? "",
					allowedToolNames: agent.allowedToolNames,
					toolApprovals: agent.toolApprovals,
				}))
				.sort((left, right) => left.name.localeCompare(right.name)),
		[agentsQuery.data],
	);

	const toolsByName = useMemo<ReadonlyMap<string, IntegrationToolFacts>>(
		() =>
			new Map(
				(catalogQuery.data?.tools ?? []).map((tool) => [
					tool.name,
					{ effectiveRequiresApproval: tool.effectiveRequiresApproval, category: tool.category },
				]),
			),
		[catalogQuery.data],
	);

	return {
		options,
		toolsByName,
		isLoading: agentsQuery.isPending || catalogQuery.isPending,
		isError: agentsQuery.isError || catalogQuery.isError,
	};
}
