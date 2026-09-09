import { ActionIcon, Group, Paper, Select, TextInput } from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { OrchestrationHandoff } from "@/features/agents/models/OrchestrationTopologyModels";

interface OrchestrationHandoffRowProps {
	edge: OrchestrationHandoff;
	/** The row's position in the handoff list — part of every data-testid and the identity the parent edits by. */
	index: number;
	endpointOptions: { value: string; label: string }[];
	/** Maps a stored endpoint id to its dropdown value (the triage rides a sentinel). */
	toEndpointValue: (id: string) => string;
	/** Maps a chosen dropdown value back to the stored endpoint id. */
	resolveEndpoint: (value: string) => string;
	onChange: (index: number, patch: Partial<OrchestrationHandoff>) => void;
	onRemove: (index: number) => void;
}

/** One explicit handoff route: from, to, an optional reason, and its remove button. */
export function OrchestrationHandoffRow({
	edge,
	index,
	endpointOptions,
	toEndpointValue,
	resolveEndpoint,
	onChange,
	onRemove,
}: OrchestrationHandoffRowProps) {
	const { t } = useTranslation();
	return (
		<Paper withBorder={true} p="xs" data-testid={`orchestration-handoff-row-${index}`}>
			<Group align="flex-end" wrap="wrap" gap="xs">
				<Select
					label={t("pages.agents.form.orchestration.handoffs.from", "From")}
					data={endpointOptions}
					value={toEndpointValue(edge.fromAgentDefinitionId)}
					allowDeselect={false}
					onChange={(value) => (value !== null ? onChange(index, { fromAgentDefinitionId: resolveEndpoint(value) }) : undefined)}
					data-testid={`orchestration-handoff-from-${index}`}
				/>
				<Select
					label={t("pages.agents.form.orchestration.handoffs.to", "To")}
					data={endpointOptions}
					value={toEndpointValue(edge.toAgentDefinitionId)}
					allowDeselect={false}
					onChange={(value) => (value !== null ? onChange(index, { toAgentDefinitionId: resolveEndpoint(value) }) : undefined)}
					data-testid={`orchestration-handoff-to-${index}`}
				/>
				<TextInput
					label={t("pages.agents.form.orchestration.handoffs.reason", "Reason (optional)")}
					value={edge.reason ?? ""}
					style={{ flex: 1, minWidth: 160 }}
					onChange={(event) =>
						onChange(index, { reason: event.currentTarget.value.length > 0 ? event.currentTarget.value : null })
					}
					data-testid={`orchestration-handoff-reason-${index}`}
				/>
				<ActionIcon
					variant="subtle"
					color="red"
					aria-label={t("pages.agents.form.orchestration.handoffs.remove", "Remove handoff")}
					onClick={() => onRemove(index)}
					data-testid={`orchestration-handoff-remove-${index}`}
				>
					<IconTrash size={16} />
				</ActionIcon>
			</Group>
		</Paper>
	);
}
