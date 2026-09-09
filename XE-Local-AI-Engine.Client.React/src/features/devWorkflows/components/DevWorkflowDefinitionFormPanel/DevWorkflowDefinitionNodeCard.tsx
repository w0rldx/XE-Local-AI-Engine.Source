import {
	ActionIcon,
	Badge,
	Group,
	MultiSelect,
	NumberInput,
	Paper,
	Select,
	Stack,
	Textarea,
	TextInput,
	Tooltip,
} from "@mantine/core";
import { IconArrowDown, IconArrowUp, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { toOptionalNumber, withCapabilities } from "@/features/devWorkflows/models/DevWorkflowDefinitionFormHelpers";
import {
	devWorkflowCapabilityReasonMaxLength,
	devWorkflowEffectsOf,
	devWorkflowNodeEffects,
} from "@/features/devWorkflows/models/DevWorkflowDefinitionValidation";
import { type DevWorkflowGraphNode, devWorkflowNodeTypes } from "@/features/devWorkflows/models/DevWorkflowModels";

/**
 * The agent surface's own set ("none" plus graded efforts, plus "auto"); an unset effort means the provider default.
 * "auto" is resolved per turn by the node into one of the others.
 */
const reasoningEfforts = ["none", "low", "medium", "high", "auto"] as const;

/** `DevWorkflowGraph.cs`'s own default is `All` for an absent policy, so those are the only two members. */
const joinPolicies = ["All", "Any"] as const;

/**
 * `DevelopmentCommandIds`, verbatim. A backend contract — a Tool node naming anything the repository's command profile
 * does not define is refused before a workspace is even prepared — so these are literals here, not a fetched list.
 */
const validationCommandIds = [
	"git_status",
	"git_diff_check",
	"dotnet_restore",
	"dotnet_build_release_no_restore",
	"dotnet_test_release_no_build",
] as const;

interface NodeOption {
	readonly id: string;
	readonly label: string;
}

export function DevWorkflowDefinitionNodeCard({
	node,
	index,
	nodeCount,
	nodeKeyOptions,
	agentOptions,
	modelOptions,
	onPatch,
	onMove,
	onRemove,
}: {
	readonly node: DevWorkflowGraphNode;
	readonly index: number;
	readonly nodeCount: number;
	readonly nodeKeyOptions: readonly string[];
	readonly agentOptions: readonly NodeOption[];
	readonly modelOptions: readonly NodeOption[];
	readonly onPatch: (patch: Partial<DevWorkflowGraphNode>) => void;
	readonly onMove: (offset: number) => void;
	readonly onRemove: () => void;
}) {
	const { t } = useTranslation();
	const nodeKey = node.nodeKey ?? "";
	const isToolNode = node.nodeType === "Tool";

	return (
		<Paper withBorder={true} p="sm" data-testid={`dev-workflow-definition-node-${index}`}>
			<Stack gap="xs">
				<Group gap="xs" wrap="nowrap" align="flex-end">
					<TextInput
						label={t("pages.devWorkflows.definition.nodeKey", "Key")}
						value={nodeKey}
						style={{ flex: 1, minWidth: 0 }}
						onChange={(event) => onPatch({ nodeKey: event.currentTarget.value })}
						data-testid={`dev-workflow-definition-node-key-${index}`}
					/>
					<Select
						label={t("pages.devWorkflows.definition.nodeType", "Type")}
						data={devWorkflowNodeTypes.map((nodeType) => ({
							value: nodeType,
							label: t(`pages.devWorkflows.nodeType.${nodeType}`, nodeType),
						}))}
						value={node.nodeType ?? "Agent"}
						allowDeselect={false}
						onChange={(value) => onPatch({ nodeType: value ?? "Agent" })}
						data-testid={`dev-workflow-definition-node-type-${index}`}
					/>
					{/* Up/down rather than a drag handle: a drag needs its own keyboard fallback anyway, and these two
					    buttons ARE that fallback with nothing extra on top. */}
					<ActionIcon
						variant="subtle"
						disabled={index === 0}
						aria-label={t("pages.devWorkflows.definition.moveUp", "Move node up")}
						onClick={() => onMove(-1)}
						data-testid={`dev-workflow-definition-node-up-${index}`}
					>
						<IconArrowUp size={16} />
					</ActionIcon>
					<ActionIcon
						variant="subtle"
						disabled={index === nodeCount - 1}
						aria-label={t("pages.devWorkflows.definition.moveDown", "Move node down")}
						onClick={() => onMove(1)}
						data-testid={`dev-workflow-definition-node-down-${index}`}
					>
						<IconArrowDown size={16} />
					</ActionIcon>
					<ActionIcon
						variant="subtle"
						color="red"
						aria-label={t("pages.devWorkflows.definition.removeNode", "Remove node")}
						onClick={onRemove}
						data-testid={`dev-workflow-definition-node-remove-${index}`}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</Group>

				<TextInput
					label={t("pages.devWorkflows.definition.nodeLabel", "Label")}
					value={node.label ?? ""}
					onChange={(event) => onPatch({ label: event.currentTarget.value })}
					data-testid={`dev-workflow-definition-node-label-${index}`}
				/>

				<Group grow={true} align="flex-start" wrap="wrap">
					<Select
						label={t("pages.devWorkflows.definition.agent", "Agent")}
						placeholder={t("pages.devWorkflows.definition.agentPlaceholder", "From the template's own seed")}
						data={agentOptions.map((agent) => ({ value: agent.id, label: agent.label }))}
						value={node.agentDefinitionId ?? null}
						clearable={true}
						searchable={true}
						onChange={(value) => onPatch({ agentDefinitionId: value })}
						data-testid={`dev-workflow-definition-node-agent-${index}`}
					/>
					{/* Only an Agent node dispatches on these: its work session is created and resumed pinned to them. A Tool
					    node runs commands and a DevTask node hands off to Dev Mode's own coder, neither of which reads
					    either field — so offering the pickers there would be the same false promise the controls were
					    pulled for. Anything already authored elsewhere still round-trips untouched. */}
					{(node.nodeType ?? "Agent") === "Agent" && (
						<>
							<Select
								label={t("pages.devWorkflows.definition.modelProfile", "Model")}
								placeholder={t("pages.devWorkflows.definition.modelPlaceholder", "Node default")}
								data={modelOptions.map((model) => ({ value: model.id, label: model.label }))}
								value={node.modelProfile ?? null}
								clearable={true}
								searchable={true}
								onChange={(value) => onPatch({ modelProfile: value })}
								data-testid={`dev-workflow-definition-node-model-${index}`}
							/>
							<Select
								label={t("pages.devWorkflows.definition.reasoningEffort", "Reasoning effort")}
								placeholder={t("pages.devWorkflows.definition.reasoningPlaceholder", "Provider default")}
								data={reasoningEfforts.map((effort) => ({ value: effort, label: effort }))}
								value={node.reasoningEffort ?? null}
								clearable={true}
								onChange={(value) => onPatch({ reasoningEffort: value })}
								data-testid={`dev-workflow-definition-node-effort-${index}`}
							/>
						</>
					)}
				</Group>

				<Textarea
					label={t("pages.devWorkflows.definition.instructions", "Instructions")}
					value={node.instructions ?? ""}
					autosize={true}
					minRows={2}
					maxRows={8}
					onChange={(event) => onPatch({ instructions: event.currentTarget.value })}
					data-testid={`dev-workflow-definition-node-instructions-${index}`}
				/>

				{isToolNode ? (
					<MultiSelect
						label={t("pages.devWorkflows.definition.validationCommands", "Validation commands")}
						description={t(
							"pages.devWorkflows.definition.validationCommandsHint",
							"Leave empty to run whatever the repository's command profile declares.",
						)}
						data={[...validationCommandIds]}
						value={[...(node.validationCommandIds ?? [])]}
						clearable={true}
						onChange={(value) => onPatch({ validationCommandIds: value })}
						data-testid={`dev-workflow-definition-node-commands-${index}`}
					/>
				) : null}

				<Group grow={true} align="flex-start" wrap="wrap">
					<Select
						label={t("pages.devWorkflows.definition.joinPolicy", "Join policy")}
						placeholder={t("pages.devWorkflows.definition.joinPolicyPlaceholder", "All")}
						data={[...joinPolicies]}
						value={node.joinPolicy ?? null}
						clearable={true}
						onChange={(value) => onPatch({ joinPolicy: value })}
						data-testid={`dev-workflow-definition-node-join-${index}`}
					/>
					<NumberInput
						label={t("pages.devWorkflows.definition.maxAttempts", "Max attempts")}
						value={node.maxAttempts ?? ""}
						min={1}
						allowDecimal={false}
						onChange={(value) => onPatch({ maxAttempts: toOptionalNumber(value) })}
						data-testid={`dev-workflow-definition-node-attempts-${index}`}
					/>
					<NumberInput
						label={t("pages.devWorkflows.definition.retryDelaySeconds", "Retry delay (s)")}
						value={node.retryDelaySeconds ?? ""}
						min={0}
						allowDecimal={false}
						onChange={(value) => onPatch({ retryDelaySeconds: toOptionalNumber(value) })}
						data-testid={`dev-workflow-definition-node-retry-delay-${index}`}
					/>
					<NumberInput
						label={t("pages.devWorkflows.definition.nodeTimeoutSeconds", "Timeout (s)")}
						value={node.nodeTimeoutSeconds ?? ""}
						min={1}
						allowDecimal={false}
						onChange={(value) => onPatch({ nodeTimeoutSeconds: toOptionalNumber(value) })}
						data-testid={`dev-workflow-definition-node-timeout-${index}`}
					/>
					<Select
						label={t("pages.devWorkflows.definition.retryTarget", "Retry target")}
						placeholder={t("pages.devWorkflows.definition.retryTargetPlaceholder", "Retry this node itself")}
						data={nodeKeyOptions.filter((key) => key !== nodeKey)}
						value={node.retryTarget ?? null}
						clearable={true}
						// The cap counts routes to a retry target, so clearing the target takes the cap with it: the server
						// refuses a `maxLoopIterations` on a node that routes none, and leaving it behind would 400 a save
						// over a field the form had just hidden.
						onChange={(value) => onPatch(value ? { retryTarget: value } : { retryTarget: null, maxLoopIterations: null })}
						data-testid={`dev-workflow-definition-node-retry-target-${index}`}
					/>
					{node.retryTarget ? (
						<NumberInput
							label={t("pages.devWorkflows.definition.maxLoopIterations", "Fix-loop cap")}
							placeholder={t("pages.devWorkflows.definition.maxLoopIterationsPlaceholder", "No cap")}
							description={t(
								"pages.devWorkflows.definition.maxLoopIterationsHelp",
								"How many times this node may route back before the run stops and asks you. An operator retry does not count.",
							)}
							value={node.maxLoopIterations ?? ""}
							min={1}
							allowDecimal={false}
							onChange={(value) => onPatch({ maxLoopIterations: toOptionalNumber(value) })}
							data-testid={`dev-workflow-definition-node-max-loops-${index}`}
						/>
					) : null}
				</Group>

				{/* An AGENT node is the only one whose reach is declared: every other type says what it does in the node
				    itself, and the server refuses a declaration anywhere else. A declared write then needs a human gate on
				    every path into this node, or the template's own waiver. */}
				{(node.nodeType ?? "Agent") === "Agent" ? (
					<Stack gap={4}>
						<MultiSelect
							label={t("pages.devWorkflows.definition.requiredCapabilities", "Declared capabilities")}
							description={t(
								"pages.devWorkflows.definition.requiredCapabilitiesHint",
								"What this node's agent is allowed to do beyond reading. Declaring a write needs a human gate on every path into it.",
							)}
							// The wire tokens themselves, like the operator and command pickers beside them: this field is a
							// declaration in the server's own vocabulary, and the sentence that explains each one is the badge
							// below and the tooltip on it.
							data={[...devWorkflowNodeEffects]}
							value={Object.keys(node.requiredCapabilities ?? {})}
							clearable={true}
							onChange={(values) => onPatch({ requiredCapabilities: withCapabilities(node.requiredCapabilities, values) })}
							data-testid={`dev-workflow-definition-node-capabilities-${index}`}
						/>
						{Object.entries(node.requiredCapabilities ?? {}).map(([effect, reason], reasonIndex) => (
							<TextInput
								key={effect}
								label={t("pages.devWorkflows.definition.capabilityReason", "Why {{effect}}?", { effect })}
								value={reason ?? ""}
								maxLength={devWorkflowCapabilityReasonMaxLength}
								// A declared effect widens what the node may do, so the definition has to say what for —
								// the server refuses an empty one, and this is where the operator can still see why.
								error={
									(reason ?? "").trim().length === 0
										? t("pages.devWorkflows.definition.capabilityReasonRequired", "A declared capability needs a reason.")
										: undefined
								}
								onChange={(event) =>
									onPatch({
										requiredCapabilities: { ...(node.requiredCapabilities ?? {}), [effect]: event.currentTarget.value },
									})
								}
								data-testid={`dev-workflow-definition-node-capability-reason-${reasonIndex}-${index}`}
							/>
						))}
					</Stack>
				) : null}

				{/* What this node can CHANGE, derived the way the invariants derive it — declared for an Agent and read
				    off the node for every other type. Not a second opinion the operator has to reconcile with the 400. */}
				<Group gap="xs" wrap="wrap" data-testid={`dev-workflow-definition-node-effects-${index}`}>
					{devWorkflowEffectsOf(node).map((effect) => (
						<Tooltip
							key={effect}
							label={
								node.requiredCapabilities?.[effect] ??
								t("pages.devWorkflows.definition.effectDerived", "Follows from what this node runs.")
							}
							withArrow={true}
						>
							<Badge size="xs" variant="light" color="gray">
								{t(`pages.devWorkflows.definition.effects.${effect}`, effect)}
							</Badge>
						</Tooltip>
					))}
				</Group>

				{/* Round-tripped, never edited. Both are authoring the RUNTIME dispatches on — an apply node's gating
				    chain, a decomposition's child budget — and a form that offered them would have to mirror server rules
				    this one deliberately does not. Shown so the operator knows they are there. */}
				<Group gap="xs" wrap="wrap" data-testid={`dev-workflow-definition-node-readonly-${index}`}>
					{node.toolMode ? (
						<Badge size="xs" variant="outline" color="gray">
							{t("pages.devWorkflows.definition.toolMode", "tool mode: {{mode}}", { mode: node.toolMode })}
						</Badge>
					) : null}
					{node.materialization ? (
						<Badge size="xs" variant="outline" color="gray">
							{t("pages.devWorkflows.definition.materialization", "materializes {{template}} (max {{max}})", {
								template: node.materialization.templateNodeKey ?? "",
								max: node.materialization.maxChildren ?? 0,
							})}
						</Badge>
					) : null}
				</Group>
			</Stack>
		</Paper>
	);
}
