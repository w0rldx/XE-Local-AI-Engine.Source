import { ActionIcon, Alert, Group, MultiSelect, NumberInput, Paper, Select, Stack, Switch, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconPlus, IconTrash } from "@tabler/icons-react";
import { useCallback, useMemo } from "react";
import { useTranslation } from "react-i18next";

import type { AgentDefinition } from "@/features/agents/models/AgentDefinitionModels";
import type { OrchestrationHandoff, OrchestrationTopology } from "@/features/agents/models/OrchestrationTopologyModels";
import { isModelToolCapable } from "@/features/agents/models/ToolCapability";

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

const TRIAGE_OPTION_VALUE = "__triage__";

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

	// Specialist candidates exclude self (the orchestrator is the triage, never its own specialist).
	const participantOptions = useMemo(
		() =>
			candidateDefinitions
				.filter((definition) => definition.id !== selfId)
				.map((definition) => ({ value: definition.id, label: definition.name })),
		[candidateDefinitions, selfId],
	);

	const definitionsById = useMemo(() => {
		const map = new Map<string, AgentDefinition>();
		for (const definition of candidateDefinitions) {
			map.set(definition.id, definition);
		}
		return map;
	}, [candidateDefinitions]);

	// Edge endpoint options = the triage (this definition) plus every selected specialist. The triage uses a stable
	// sentinel value that serializes back to selfId so edges can reference the orchestrator before it has an id.
	const edgeEndpointData = useMemo(() => {
		const triageLabel = t("pages.agents.form.orchestration.triageOptionLabel", "{{name}} (triage)", {
			name: triageName.trim().length > 0 ? triageName.trim() : t("pages.agents.form.orchestration.thisAgent", "This agent"),
		});
		const specialists = topology.participantAgentDefinitionIds.map((id) => ({
			value: id,
			label: definitionsById.get(id)?.name ?? id,
		}));
		return [{ value: TRIAGE_OPTION_VALUE, label: triageLabel }, ...specialists];
	}, [definitionsById, t, topology.participantAgentDefinitionIds, triageName]);

	// Resolve an edge endpoint value (sentinel or specialist id) to the stored id (selfId for the triage sentinel).
	const resolveEndpoint = useCallback((value: string) => (value === TRIAGE_OPTION_VALUE ? selfId : value), [selfId]);

	// Resolve a stored endpoint id back to the dropdown value (the triage/self id maps to the sentinel).
	const toEndpointValue = useCallback(
		(id: string) => (id === selfId || id === "" ? TRIAGE_OPTION_VALUE : id),
		[selfId],
	);

	// Models in play: the orchestrator's own + every selected participant's. Any incapable model degrades the run.
	const incapableModels = useMemo(() => {
		if (toolCapableModels.length === 0) {
			return [] as string[];
		}
		const offenders: string[] = [];
		if (!isModelToolCapable(orchestratorModelProfile, toolCapableModels)) {
			offenders.push(orchestratorModelProfile ?? "");
		}
		for (const id of topology.participantAgentDefinitionIds) {
			const model = definitionsById.get(id)?.modelProfile ?? null;
			if (!isModelToolCapable(model, toolCapableModels) && model !== null) {
				offenders.push(model);
			}
		}
		return Array.from(new Set(offenders.filter((model) => model.length > 0)));
	}, [definitionsById, orchestratorModelProfile, toolCapableModels, topology.participantAgentDefinitionIds]);

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
				<Alert
					color="yellow"
					icon={<IconAlertTriangle size={16} />}
					data-testid="orchestration-capability-warning"
				>
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
					<Paper
						withBorder={true}
						p="xs"
						key={`${edge.fromAgentDefinitionId}->${edge.toAgentDefinitionId}`}
						data-testid={`orchestration-handoff-row-${index}`}
					>
						<Group align="flex-end" wrap="wrap" gap="xs">
							<Select
								label={t("pages.agents.form.orchestration.handoffs.from", "From")}
								data={edgeEndpointData}
								value={toEndpointValue(edge.fromAgentDefinitionId)}
								allowDeselect={false}
								onChange={(value) =>
									value !== null
										? handleEdgeChange(index, { fromAgentDefinitionId: resolveEndpoint(value) })
										: undefined
								}
								data-testid={`orchestration-handoff-from-${index}`}
							/>
							<Select
								label={t("pages.agents.form.orchestration.handoffs.to", "To")}
								data={edgeEndpointData}
								value={toEndpointValue(edge.toAgentDefinitionId)}
								allowDeselect={false}
								onChange={(value) =>
									value !== null
										? handleEdgeChange(index, { toAgentDefinitionId: resolveEndpoint(value) })
										: undefined
								}
								data-testid={`orchestration-handoff-to-${index}`}
							/>
							<TextInput
								label={t("pages.agents.form.orchestration.handoffs.reason", "Reason (optional)")}
								value={edge.reason ?? ""}
								style={{ flex: 1, minWidth: 160 }}
								onChange={(event) =>
									handleEdgeChange(index, {
										reason: event.currentTarget.value.length > 0 ? event.currentTarget.value : null,
									})
								}
								data-testid={`orchestration-handoff-reason-${index}`}
							/>
							<ActionIcon
								variant="subtle"
								color="red"
								aria-label={t("pages.agents.form.orchestration.handoffs.remove", "Remove handoff")}
								onClick={() => handleRemoveHandoff(index)}
								data-testid={`orchestration-handoff-remove-${index}`}
							>
								<IconTrash size={16} />
							</ActionIcon>
						</Group>
					</Paper>
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
