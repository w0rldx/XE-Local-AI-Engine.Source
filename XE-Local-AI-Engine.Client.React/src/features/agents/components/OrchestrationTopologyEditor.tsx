import { ActionIcon, Alert, Group, MultiSelect, NumberInput, Stack, Switch, Text } from "@mantine/core";
import { IconAlertTriangle, IconPlus } from "@tabler/icons-react";
import { useCallback, useMemo } from "react";
import { useTranslation } from "react-i18next";

import { OrchestrationHandoffRow } from "@/features/agents/components/OrchestrationTopologyEditor/OrchestrationHandoffRow";
import type { AgentDefinition } from "@/features/agents/models/AgentDefinitionModels";
import {
	incapableOrchestrationModels,
	orchestrationDefinitionsById,
	orchestrationEndpointOptions,
	orchestrationParticipantOptions,
	resolveOrchestrationEndpoint,
	toOrchestrationEndpointValue,
} from "@/features/agents/models/OrchestrationTopologyEditorDerivations";
import type { OrchestrationHandoff, OrchestrationTopology } from "@/features/agents/models/OrchestrationTopologyModels";

interface OrchestrationTopologyEditorProps {
	// The current structured topology (specialists + edges + knobs). Owned by the parent form.
	topology: OrchestrationTopology;
	// All agent definitions the user could pick as participants. The editing definition (self) is the triage and is
	// excluded from the specialist list. On create there is no self id, so nothing is excluded.
	candidateDefinitions: readonly AgentDefinition[];
	// The editing definition's id (self / triage). Empty string on create.
	selfId: string;
	// The triage's display name (this definition's name as typed in the form) and the orchestrator's selected model
	// — used for the capability warning and to label the triage in edge dropdowns.
	triageName: string;
	orchestratorModelProfile: string | null;
	// Backend tool-capable model list (AgentHomeOptions.ToolCapableModels). Empty = capability source unavailable.
	toolCapableModels: readonly string[];
	// Validation error for the participants field (e.g. "needs at least one specialist"), surfaced by the parent.
	participantsError?: string;
	onChange: (topology: OrchestrationTopology) => void;
}

// Topology authoring section shown when kind=Orchestrator (orchestration). The orchestrator definition itself is the
// triage; the user picks specialist participants, optionally draws handoff edges between them (empty = mesh auto),
// and sets the turn cap / return-to-previous knob. A prominent warning surfaces if the orchestrator or any selected
// participant is pinned to a model that is not tool-capable — orchestration degrades to a single agent in that case.
export function OrchestrationTopologyEditor({
	topology,
	candidateDefinitions,
	selfId,
	triageName,
	orchestratorModelProfile,
	toolCapableModels,
	participantsError,
	onChange,
}: OrchestrationTopologyEditorProps) {
	const { t } = useTranslation();

	const participantOptions = useMemo(
		() => orchestrationParticipantOptions(candidateDefinitions, selfId),
		[candidateDefinitions, selfId],
	);
	const definitionsById = useMemo(() => orchestrationDefinitionsById(candidateDefinitions), [candidateDefinitions]);

	const edgeEndpointData = useMemo(() => {
		const triageLabel = t("pages.agents.form.orchestration.triageOptionLabel", "{{name}} (triage)", {
			name: triageName.trim().length > 0 ? triageName.trim() : t("pages.agents.form.orchestration.thisAgent", "This agent"),
		});
		return orchestrationEndpointOptions(triageLabel, topology.participantAgentDefinitionIds, definitionsById);
	}, [definitionsById, t, topology.participantAgentDefinitionIds, triageName]);

	const resolveEndpoint = useCallback((value: string) => resolveOrchestrationEndpoint(value, selfId), [selfId]);
	const toEndpointValue = useCallback((id: string) => toOrchestrationEndpointValue(id, selfId), [selfId]);

	const incapableModels = useMemo(
		() =>
			incapableOrchestrationModels(
				orchestratorModelProfile,
				topology.participantAgentDefinitionIds,
				definitionsById,
				toolCapableModels,
			),
		[definitionsById, orchestratorModelProfile, toolCapableModels, topology.participantAgentDefinitionIds],
	);

	const handleParticipantsChange = useCallback(
		(ids: string[]) => {
			// Dropping a specialist also drops any handoff edge that referenced it (an edge can never dangle).
			const nextSet = new Set(ids);
			const handoffs = topology.handoffs.filter((edge) => {
				const from = edge.fromAgentDefinitionId;
				const to = edge.toAgentDefinitionId;
				const fromOk = from === selfId || from === "" || nextSet.has(from);
				const toOk = to === selfId || to === "" || nextSet.has(to);
				return fromOk && toOk;
			});
			onChange({ ...topology, participantAgentDefinitionIds: ids, handoffs });
		},
		[onChange, selfId, topology],
	);

	const handleAddHandoff = useCallback(() => {
		const next: OrchestrationHandoff = {
			fromAgentDefinitionId: selfId,
			toAgentDefinitionId: topology.participantAgentDefinitionIds[0] ?? selfId,
			reason: null,
		};
		onChange({ ...topology, handoffs: [...topology.handoffs, next] });
	}, [onChange, selfId, topology]);

	const handleRemoveHandoff = useCallback(
		(index: number) => {
			onChange({ ...topology, handoffs: topology.handoffs.filter((_, i) => i !== index) });
		},
		[onChange, topology],
	);

	const handleEdgeChange = useCallback(
		(index: number, patch: Partial<OrchestrationHandoff>) => {
			const handoffs = topology.handoffs.map((edge, i) => (i === index ? { ...edge, ...patch } : edge));
			onChange({ ...topology, handoffs });
		},
		[onChange, topology],
	);

	return (
		<Stack gap="sm" data-testid="orchestration-topology-editor">
			<Text size="sm" fw={600}>
				{t("pages.agents.form.orchestration.label", "Orchestration")}
			</Text>
			<Text size="xs" c="dimmed">
				{t(
					"pages.agents.form.orchestration.description",
					"This agent acts as the triage. Pick the specialists it can hand off to and (optionally) constrain the handoff routes.",
				)}
			</Text>

			{incapableModels.length > 0 ? (
				<Alert color="yellow" icon={<IconAlertTriangle size={16} />} data-testid="orchestration-capability-warning">
					{t(
						"pages.agents.form.orchestration.degradeWarning",
						"One or more models in this orchestration are not tool-capable ({{models}}). Orchestration routing requires tool calling, so this definition will run as a single agent instead.",
						{ models: incapableModels.join(", ") },
					)}
				</Alert>
			) : null}

			<MultiSelect
				label={t("pages.agents.form.orchestration.participants.label", "Specialist participants")}
				placeholder={t("pages.agents.form.orchestration.participants.placeholder", "Pick agents to hand off to")}
				data={participantOptions}
				value={topology.participantAgentDefinitionIds}
				searchable={true}
				error={participantsError}
				nothingFoundMessage={t(
					"pages.agents.form.orchestration.participants.empty",
					"No other agents available. Create more agents to use as participants.",
				)}
				onChange={handleParticipantsChange}
				data-testid="orchestration-participants"
			/>

			<Stack gap="xs">
				<Group justify="space-between" align="center">
					<Text size="sm" fw={600}>
						{t("pages.agents.form.orchestration.handoffs.label", "Handoff routes")}
					</Text>
					<ActionIcon
						variant="light"
						aria-label={t("pages.agents.form.orchestration.handoffs.add", "Add handoff")}
						disabled={topology.participantAgentDefinitionIds.length === 0}
						onClick={handleAddHandoff}
						data-testid="orchestration-add-handoff"
					>
						<IconPlus size={16} />
					</ActionIcon>
				</Group>

				{topology.handoffs.length === 0 ? (
					<Text size="xs" c="dimmed" data-testid="orchestration-mesh-hint">
						{t(
							"pages.agents.form.orchestration.handoffs.meshHint",
							"Mesh (auto): with no explicit routes, every agent can hand off to every other.",
						)}
					</Text>
				) : null}

				{/* A 2-agent mesh (triage + 1 specialist) self-loops in MAF — real mesh routing needs 3+ agents. Nudge
				    the user to draw an explicit triage→specialist route in that case. */}
				{topology.handoffs.length === 0 && topology.participantAgentDefinitionIds.length < 2 ? (
					<Text size="xs" c="orange" data-testid="orchestration-two-agent-hint">
						{t(
							"pages.agents.form.orchestration.handoffs.twoAgentHint",
							"Mesh routing is meaningful with 3+ agents; with 2 agents add an explicit handoff route.",
						)}
					</Text>
				) : null}

				{topology.handoffs.map((edge, index) => (
					// Content-derived key: handoff routes are unique by (from, to), so this is stable across reorders
					// and edits without needing a server id, and avoids index-as-key state-shift bugs on removal.
					<OrchestrationHandoffRow
						key={`${edge.fromAgentDefinitionId}->${edge.toAgentDefinitionId}`}
						edge={edge}
						index={index}
						endpointOptions={edgeEndpointData}
						toEndpointValue={toEndpointValue}
						resolveEndpoint={resolveEndpoint}
						onChange={handleEdgeChange}
						onRemove={handleRemoveHandoff}
					/>
				))}
			</Stack>

			<Group grow={true} align="flex-start">
				<NumberInput
					label={t("pages.agents.form.orchestration.maxTurns.label", "Max turns per agent")}
					description={t(
						"pages.agents.form.orchestration.maxTurns.description",
						"Caps how many times one agent is re-invoked before routing must move on.",
					)}
					min={1}
					max={64}
					allowDecimal={false}
					value={topology.maxTurnsPerAgent}
					onChange={(value) =>
						onChange({
							...topology,
							maxTurnsPerAgent: typeof value === "number" && Number.isFinite(value) ? value : topology.maxTurnsPerAgent,
						})
					}
					data-testid="orchestration-max-turns"
				/>
				<Switch
					mt="xl"
					label={t("pages.agents.form.orchestration.returnToPrevious.label", "Return to previous specialist")}
					description={t(
						"pages.agents.form.orchestration.returnToPrevious.description",
						"Route the next user turn back to the specialist that handled the last turn, instead of through triage.",
					)}
					checked={topology.returnToPrevious}
					onChange={(event) => onChange({ ...topology, returnToPrevious: event.currentTarget.checked })}
					data-testid="orchestration-return-to-previous"
				/>
			</Group>
		</Stack>
	);
}
