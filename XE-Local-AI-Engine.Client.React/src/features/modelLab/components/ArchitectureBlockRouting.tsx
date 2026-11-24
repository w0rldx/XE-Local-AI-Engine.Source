import { Group, Stack, Text } from "@mantine/core";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";

import { getModelLabExperimentRoutingOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { BasisBadge } from "@/features/modelLab/components/ArchitectureExplanation";
import { expertUsage } from "@/features/modelLab/models/ModelLabRouting";
import {
	routingProvenanceNote,
	routingUsageCount,
	routingUsageTitle,
} from "@/features/modelLab/models/ModelLabRoutingProvenance";

interface ArchitectureBlockRoutingProps {
	// The trace laid over the blocks.
	traceId: string;
	blockIndex: number;
}

// How often this block chose each expert over the overlaid run. Mounted only for a block the explorer's routed set holds,
// so the run's routing is already cached and names this block in a saved step.
export function ArchitectureBlockRouting({ traceId, blockIndex }: ArchitectureBlockRoutingProps) {
	const { t } = useTranslation();
	// The stepper's query: one cache entry per run, whichever view asks first.
	const routing = useQuery({
		...getModelLabExperimentRoutingOptions({ path: { experimentId: traceId } }),
		// A saved run's routing never changes.
		staleTime: Number.POSITIVE_INFINITY,
		// A dense or unrouted run answers 404; every block change remounts this panel and must not ask again. The
		// stepper's panel keeps retrying, and it is where a real failure is shown.
		retryOnMount: false,
	});
	const data = routing.data;
	if (data === undefined) {
		return null;
	}
	const usage = expertUsage(data.steps, blockIndex);

	return (
		<Stack gap={4} data-testid="model-lab-architecture-routing">
			<Text size="sm" fw={600}>
				{t("pages.modelLab.architecture.routing.title", "Experts this block chose in the laid-over run")}
			</Text>
			<Group gap="xs">
				<Text size="sm">{routingUsageTitle(t, data)}</Text>
				<BasisBadge basis="derived" />
			</Group>
			<Text size="xs" c="dimmed" data-testid="model-lab-architecture-routing-note">
				{routingProvenanceNote(t)}
			</Text>
			{usage.slice(0, data.expertsPerToken).map((entry) => (
				<Text key={entry.expert} size="sm" ff="monospace" data-testid="model-lab-architecture-routing-usage-row">
					{routingUsageCount(t, entry, data)}
				</Text>
			))}
		</Stack>
	);
}
