import {
	ActionIcon,
	Alert,
	Badge,
	Button,
	Group,
	Loader,
	MultiSelect,
	NumberInput,
	Paper,
	Select,
	Stack,
	Switch,
	Table,
	Text,
	Textarea,
	TextInput,
	Tooltip,
} from "@mantine/core";
import { IconAlertTriangle, IconArchive, IconArrowDown, IconArrowUp, IconPlus, IconTrash } from "@tabler/icons-react";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { readDevWorkflowConflict } from "@/features/devWorkflows/api/DevWorkflowConflict";
import {
	devWorkflowCapabilityReasonMaxLength,
	devWorkflowEffectsOf,
	devWorkflowNodeEffects,
	validateDevWorkflowGraph,
} from "@/features/devWorkflows/models/DevWorkflowDefinitionValidation";
import {
	type DevWorkflowGraph,
	type DevWorkflowGraphEdge,
	type DevWorkflowGraphNode,
	devWorkflowNodeTypes,
} from "@/features/devWorkflows/models/DevWorkflowModels";
import {
	useDevWorkflowAgentOptions,
	useDevWorkflowDefinition,
	useDevWorkflowDefinitionMutations,
	useDevWorkflowModelOptions,
} from "@/features/devWorkflows/queries/useDevWorkflows";

/**
 * The agent surface's own set ("none" plus graded efforts, plus "auto"); an unset effort means the provider default.
 * "auto" is resolved per turn by the node into one of the others.
 */
const reasoningEfforts = ["none", "low", "medium", "high", "auto"] as const;

/** `DevWorkflowGraph.cs`'s own default is `All` for an absent policy, so those are the only two members. */
const joinPolicies = ["All", "Any"] as const;

/**
 * `DevWorkflowConditionOperator`, in the lowercase spelling the seeded templates use. The server parses it
 * case-insensitively and REFUSES anything outside the set, so this is a picker for the same reason `nodeTypes` is: a
 * token nothing parses is a definition whose routing nobody can predict, caught at save rather than at run start.
 */
const conditionOperators = ["eq", "ne", "gt", "gte", "lt", "lte", "exists", "notExists"] as const;

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

/** One editable row and the id it keeps for as long as this editing session lasts. */
interface DraftRow<T> {
	readonly id: string;
	readonly value: T;
}

function toRow<T>(value: T): DraftRow<T> {
	return { id: crypto.randomUUID(), value };
}

export interface DevWorkflowDefinitionFormPanelProps {
	readonly definitionId: string | undefined;
}

/**
 * The definition EDITOR (P4 §2.9, D row): a form over the stored graph document, deliberately not a canvas (N1/X16).
 *
 * Editing is scoped to the fields an operator authors — the node's identity, its binding to an agent, its per-node
 * model and effort overrides, its retry budget and target, and the edges between nodes. `toolMode`,
 * `materialization` and `requiredCapabilities` are shown as read-only badges and ROUND-TRIPPED untouched: the wire
 * DTO is a field-for-field mirror of the stored document, so a definition read back and saved keeps every field it
 * arrived with, and a form that dropped one would quietly delete authoring the runtime depends on.
 *
 * `modelProfile` and `reasoningEffort` ARE dispatched on, and only on an Agent node: that lane creates and resumes the
 * node's work session pinned to them, over the bound agent's own configuration. The model picker offers this node's
 * installed CHAT models, the same list the chat picker uses. A graph may still name one that is gone by the time it
 * runs, and that fails the node run the way a stale pin on an agent definition does — nothing is silently swapped.
 *
 * There is no "new definition" here. The seeder skips slugs that already exist, so an edited template survives a
 * restart, and creating a graph from an empty form is a job for a canvas that D does not ship.
 */
export function DevWorkflowDefinitionFormPanel({ definitionId }: DevWorkflowDefinitionFormPanelProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const definitionQuery = useDevWorkflowDefinition(definitionId);
	const { update, archive } = useDevWorkflowDefinitionMutations();
	const agentOptions = useDevWorkflowAgentOptions();
	const modelOptions = useDevWorkflowModelOptions();

	const [name, setName] = useState("");
	// Rows carry a client id for the whole editing session. The node KEY is a field being edited and an array index
	// moves when a row does, so neither can be a React key: one remounts the input on every keystroke and takes the
	// caret with it, the other hands a reordered row the state of the row it displaced.
	const [nodeRows, setNodeRows] = useState<readonly DraftRow<DevWorkflowGraphNode>[]>([]);
	const [edgeRows, setEdgeRows] = useState<readonly DraftRow<DevWorkflowGraphEdge>[]>([]);
	const [schemaVersion, setSchemaVersion] = useState(1);
	// The graph-level waiver of GRAPH-C4-2, held as a boolean and sent back as `true` or not at all: a template that
	// never waived anything must not GAIN an explicit `false`, which would rewrite a stored document to say something
	// it never said.
	const [allowUngatedWrites, setAllowUngatedWrites] = useState(false);
	const [saveError, setSaveError] = useState<string | undefined>(undefined);
	const [isConflict, setIsConflict] = useState(false);

	const definition = definitionQuery.data;
	// Seeded when the definition lands and reseeded when its version moves — which is what a successful save does, and
	// what a reload after a 409 does. Keyed on identity rather than on the object so typing is never overwritten.
	const seedKey = `${definition?.id ?? ""}:${definition?.version ?? 0}`;
	// biome-ignore lint/correctness/useExhaustiveDependencies: seeding is keyed on identity, not on the document object.
	useEffect(() => {
		setName(definition?.name ?? "");
		setSchemaVersion(definition?.graph?.schemaVersion ?? 1);
		setAllowUngatedWrites(definition?.graph?.allowUngatedWrites === true);
		setNodeRows((definition?.graph?.nodes ?? []).map(toRow));
		setEdgeRows((definition?.graph?.edges ?? []).map(toRow));
		setSaveError(undefined);
		setIsConflict(false);
	}, [seedKey]);

	const nodes = nodeRows.map((row) => row.value);
	// Deduplicated: two nodes sharing a key is a state the editor must be able to RENDER (it is one of the issues it
	// reports), and Mantine refuses a Select whose options repeat a value.
	const nodeKeyOptions = [...new Set(nodes.map((node) => node.nodeKey ?? "").filter((key) => key.length > 0))];
	const graph: DevWorkflowGraph = useMemo(
		() => ({
			schemaVersion,
			nodes: nodeRows.map((row) => row.value),
			edges: edgeRows.map((row) => row.value),
			allowUngatedWrites: allowUngatedWrites ? true : undefined,
		}),
		[schemaVersion, nodeRows, edgeRows, allowUngatedWrites],
	);
	const issues = useMemo(() => validateDevWorkflowGraph(graph), [graph]);

	const patchNode = (id: string, patch: Partial<DevWorkflowGraphNode>): void =>
		setNodeRows((current) => current.map((row) => (row.id === id ? { ...row, value: { ...row.value, ...patch } } : row)));

	const moveNode = (id: string, offset: number): void =>
		setNodeRows((current) => {
			const index = current.findIndex((row) => row.id === id);
			const list = [...current];
			const moved = list[index];
			const displaced = list[index + offset];
			if (!moved || !displaced) {
				return current;
			}
			list[index] = displaced;
			list[index + offset] = moved;
			return list;
		});

	const patchEdge = (id: string, patch: Partial<DevWorkflowGraphEdge>): void =>
		setEdgeRows((current) => current.map((row) => (row.id === id ? { ...row, value: { ...row.value, ...patch } } : row)));

	const handleSave = async (): Promise<void> => {
		setSaveError(undefined);
		setIsConflict(false);
		try {
			await update.mutateAsync({
				path: { definitionId: definitionId ?? "" },
				// The version this edit was made from (X5). Without it the PUT is a last-writer-wins overwrite.
				body: { version: definition?.version ?? 0, name: name.trim(), graph },
			});
		} catch (error) {
			if (readDevWorkflowConflict(error)) {
				setIsConflict(true);
				return;
			}
			// Everything else is the server's own refusal — a graph rule this form does not mirror, most likely — so its
			// problem detail is rendered verbatim rather than replaced with a sentence that names no field.
			setSaveError(apiErrorMessage(error, t("pages.devWorkflows.definition.saveFailed", "Could not save this template.")));
		}
	};

	const handleArchive = async (): Promise<void> => {
		const confirmed = await confirm({
			title: t("pages.devWorkflows.definition.archiveTitle", "Archive this template?"),
			description: t(
				"pages.devWorkflows.definition.archiveDescription",
				"It disappears from the picker and no new run can start on it. Runs that already pinned a snapshot of it are untouched.",
			),
			confirmationText: t("pages.devWorkflows.definition.archiveConfirm", "Archive"),
			cancellationText: t("common.cancel", "Cancel"),
		});
		if (!confirmed) {
			return;
		}
		setSaveError(undefined);
		try {
			await archive.mutateAsync({ path: { definitionId: definitionId ?? "" } });
		} catch (error) {
			setSaveError(apiErrorMessage(error, t("pages.devWorkflows.definition.archiveFailed", "Could not archive this template.")));
		}
	};

	if (!definitionId) {
		return (
			<EmptyState
				message={t("pages.devWorkflows.definition.pickToEdit", "Pick a template to edit the nodes and edges it runs.")}
				data-testid="dev-workflow-definition-form-empty"
			/>
		);
	}
	if (definitionQuery.isPending) {
		return <Loader size="sm" data-testid="dev-workflow-definition-form-loading" />;
	}
	if (definitionQuery.isError) {
		return (
			<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-definition-form-error">
				{apiErrorMessage(definitionQuery.error, t("pages.devWorkflows.definition.loadFailed", "Could not load this template."))}
			</Alert>
		);
	}

	return (
		<Stack gap="md" data-testid="dev-workflow-definition-form">
			<Group gap="xs" align="flex-end" wrap="wrap">
				<TextInput
					label={t("pages.devWorkflows.definition.nameLabel", "Name")}
					value={name}
					required={true}
					style={{ flex: 1, minWidth: 240 }}
					onChange={(event) => setName(event.currentTarget.value)}
					data-testid="dev-workflow-definition-name"
				/>
				<Badge size="sm" variant="light" color="gray" data-testid="dev-workflow-definition-form-version">
					{t("pages.devWorkflows.definition.version", "v{{version}}", { version: definition?.version ?? 1 })}
				</Badge>
				{/* The waiver is the TEMPLATE saying once, and in writing, that a node here may write to the repository
				    with no operator asked — rather than each node quietly opting itself out. */}
				<Switch
					label={t("pages.devWorkflows.definition.allowUngatedWrites", "Allow ungated writes")}
					description={t(
						"pages.devWorkflows.definition.allowUngatedWritesHelp",
						"Lets a node in this template write to the repository without a human gate on every path into it.",
					)}
					checked={allowUngatedWrites}
					onChange={(event) => setAllowUngatedWrites(event.currentTarget.checked)}
					data-testid="dev-workflow-definition-allow-ungated-writes"
				/>
				<Button
					variant="light"
					color="red"
					leftSection={<IconArchive size={16} />}
					loading={archive.isPending}
					onClick={() => {
						handleArchive().catch(() => undefined);
					}}
					data-testid="dev-workflow-definition-archive"
				>
					{t("pages.devWorkflows.definition.archive", "Archive")}
				</Button>
				<Button
					loading={update.isPending}
					disabled={issues.length > 0 || name.trim().length === 0}
					onClick={() => {
						handleSave().catch(() => undefined);
					}}
					data-testid="dev-workflow-definition-save"
				>
					{t("common.save", "Save")}
				</Button>
			</Group>

			{isConflict ? (
				<Alert
					color="orange"
					variant="light"
					icon={<IconAlertTriangle size={16} />}
					data-testid="dev-workflow-definition-conflict"
				>
					<Stack gap="sm" align="flex-start">
						<Text size="sm">
							{t(
								"pages.devWorkflows.definition.conflict",
								"This template changed elsewhere. Reload it to edit the current version — saving over it would discard that change.",
							)}
						</Text>
						<Button
							size="xs"
							variant="light"
							onClick={() => {
								definitionQuery.refetch().catch(() => undefined);
							}}
							data-testid="dev-workflow-definition-reload"
						>
							{t("pages.devWorkflows.definition.reload", "Reload")}
						</Button>
					</Stack>
				</Alert>
			) : null}

			{saveError ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-definition-save-error">
					{saveError}
				</Alert>
			) : null}

			{/* Checked BEFORE the save, because a 400 from the graph parser names the rule and not the row. */}
			{issues.length > 0 ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-definition-issues">
					<Stack gap={4}>
						{issues.map((issue) => (
							<Text key={`${issue.rule}:${issue.subject}`} size="sm" data-testid={`dev-workflow-definition-issue-${issue.rule}`}>
								{t(`pages.devWorkflows.definition.issues.${issue.rule}`, issue.rule, { subject: issue.subject })}
							</Text>
						))}
					</Stack>
				</Alert>
			) : null}

			<Stack gap="xs">
				<Group justify="space-between" wrap="wrap">
					<Text fw={600}>{t("pages.devWorkflows.definition.nodes", "Nodes")}</Text>
					<Button
						size="xs"
						variant="light"
						leftSection={<IconPlus size={14} />}
						onClick={() => setNodeRows((current) => [...current, toRow({ nodeKey: "", nodeType: "Agent", label: "" })])}
						data-testid="dev-workflow-definition-add-node"
					>
						{t("pages.devWorkflows.definition.addNode", "Add node")}
					</Button>
				</Group>
				{nodeRows.length === 0 ? (
					<EmptyState
						message={t("pages.devWorkflows.definition.noNodes", "This template has no nodes yet.")}
						data-testid="dev-workflow-definition-no-nodes"
					/>
				) : (
					nodeRows.map((row, index) => (
						<NodeCard
							key={row.id}
							node={row.value}
							index={index}
							nodeCount={nodeRows.length}
							nodeKeyOptions={nodeKeyOptions}
							agentOptions={agentOptions.data ?? []}
							modelOptions={modelOptions.data ?? []}
							onPatch={(patch) => patchNode(row.id, patch)}
							onMove={(offset) => moveNode(row.id, offset)}
							onRemove={() => setNodeRows((current) => current.filter((candidate) => candidate.id !== row.id))}
						/>
					))
				)}
			</Stack>

			<Stack gap="xs">
				<Group justify="space-between" wrap="wrap">
					<Text fw={600}>{t("pages.devWorkflows.definition.edges", "Edges")}</Text>
					<Button
						size="xs"
						variant="light"
						leftSection={<IconPlus size={14} />}
						onClick={() => setEdgeRows((current) => [...current, toRow({ from: "", to: "" })])}
						data-testid="dev-workflow-definition-add-edge"
					>
						{t("pages.devWorkflows.definition.addEdge", "Add edge")}
					</Button>
				</Group>
				{edgeRows.length === 0 ? (
					<EmptyState
						message={t("pages.devWorkflows.definition.noEdges", "This template has no edges yet.")}
						data-testid="dev-workflow-definition-no-edges"
					/>
				) : (
					<Table.ScrollContainer minWidth={720}>
						<Table data-testid="dev-workflow-definition-edges">
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("pages.devWorkflows.definition.edgeFrom", "From")}</Table.Th>
									<Table.Th>{t("pages.devWorkflows.definition.edgeTo", "To")}</Table.Th>
									<Table.Th>{t("pages.devWorkflows.definition.conditionPath", "Condition path")}</Table.Th>
									<Table.Th>{t("pages.devWorkflows.definition.conditionOp", "Operator")}</Table.Th>
									<Table.Th>{t("pages.devWorkflows.definition.conditionValue", "Value")}</Table.Th>
									<Table.Th />
								</Table.Tr>
							</Table.Thead>
							<Table.Tbody>
								{edgeRows.map((row, index) => (
									<EdgeRow
										key={row.id}
										edge={row.value}
										index={index}
										nodeKeyOptions={nodeKeyOptions}
										onPatch={(patch) => patchEdge(row.id, patch)}
										onRemove={() => setEdgeRows((current) => current.filter((candidate) => candidate.id !== row.id))}
									/>
								))}
							</Table.Tbody>
						</Table>
					</Table.ScrollContainer>
				)}
			</Stack>
		</Stack>
	);
}

interface NodeOption {
	readonly id: string;
	readonly label: string;
}

function NodeCard({
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

function EdgeRow({
	edge,
	index,
	nodeKeyOptions,
	onPatch,
	onRemove,
}: {
	readonly edge: DevWorkflowGraphEdge;
	readonly index: number;
	readonly nodeKeyOptions: readonly string[];
	readonly onPatch: (patch: Partial<DevWorkflowGraphEdge>) => void;
	readonly onRemove: () => void;
}) {
	const { t } = useTranslation();
	const condition = edge.condition ?? undefined;

	/**
	 * The condition is written as a whole or not at all: an edge carrying a path with no operator is a rule the
	 * runtime cannot evaluate, and clearing the path is how an operator says "always taken".
	 *
	 * `value` is only rewritten when the VALUE cell was the one edited. It is scalar JSON on the wire and the server
	 * compares by JSON kind — `Compare` answers null on any type mismatch and the edge then silently never fires — so a
	 * stored `true` must not become `"true"` because someone corrected the path beside it.
	 */
	const patchCondition = (patch: { path?: string; op?: string; value?: string }): void => {
		const path = patch.path ?? condition?.path ?? "";
		if (path.length === 0) {
			onPatch({ condition: null });
			return;
		}
		const value = "value" in patch ? parseConditionValue(patch.value ?? "") : (condition?.value ?? "");
		onPatch({ condition: { path, op: patch.op ?? condition?.op ?? "", value } });
	};

	return (
		<Table.Tr data-testid={`dev-workflow-definition-edge-${index}`}>
			<Table.Td>
				<Select
					aria-label={t("pages.devWorkflows.definition.edgeFrom", "From")}
					data={[...nodeKeyOptions]}
					value={edge.from ?? null}
					onChange={(value) => onPatch({ from: value ?? "" })}
					data-testid={`dev-workflow-definition-edge-from-${index}`}
				/>
			</Table.Td>
			<Table.Td>
				<Select
					aria-label={t("pages.devWorkflows.definition.edgeTo", "To")}
					data={[...nodeKeyOptions]}
					value={edge.to ?? null}
					onChange={(value) => onPatch({ to: value ?? "" })}
					data-testid={`dev-workflow-definition-edge-to-${index}`}
				/>
			</Table.Td>
			<Table.Td>
				<TextInput
					aria-label={t("pages.devWorkflows.definition.conditionPath", "Condition path")}
					value={condition?.path ?? ""}
					onChange={(event) => patchCondition({ path: event.currentTarget.value })}
					data-testid={`dev-workflow-definition-edge-path-${index}`}
				/>
			</Table.Td>
			<Table.Td>
				{/* The stored operator is unioned in so a definition authored by hand in another casing still shows
				    what it says and round-trips, instead of rendering as an empty picker. */}
				<Select
					aria-label={t("pages.devWorkflows.definition.conditionOp", "Operator")}
					data={[...new Set<string>([...conditionOperators, ...(condition?.op ? [condition.op] : [])])]}
					value={condition?.op ?? null}
					onChange={(value) => patchCondition({ op: value ?? "" })}
					data-testid={`dev-workflow-definition-edge-op-${index}`}
				/>
			</Table.Td>
			<Table.Td>
				<TextInput
					aria-label={t("pages.devWorkflows.definition.conditionValue", "Value")}
					value={readValue(condition?.value)}
					onChange={(event) => patchCondition({ value: event.currentTarget.value })}
					data-testid={`dev-workflow-definition-edge-value-${index}`}
				/>
			</Table.Td>
			<Table.Td>
				<ActionIcon
					variant="subtle"
					color="red"
					aria-label={t("pages.devWorkflows.definition.removeEdge", "Remove edge")}
					onClick={onRemove}
					data-testid={`dev-workflow-definition-edge-remove-${index}`}
				>
					<IconTrash size={16} />
				</ActionIcon>
			</Table.Td>
		</Table.Tr>
	);
}

/**
 * The declared set after the picker changed: a capability that was already there keeps the reason its author wrote, and
 * a newly picked one starts empty so the operator is the one who says why. Answers `null` for an empty set, which is
 * how the wire says "declares nothing" — an empty object would be a document saying something it does not mean.
 */
function withCapabilities(
	current: { readonly [key: string]: string } | null | undefined,
	effects: readonly string[],
): { readonly [key: string]: string } | null {
	if (effects.length === 0) {
		return null;
	}
	return Object.fromEntries(effects.map((effect) => [effect, current?.[effect] ?? ""]));
}

/** Mantine's NumberInput answers "" for an emptied field; the wire wants `null` there, not `NaN` and not `0`. */
function toOptionalNumber(value: string | number): number | null {
	if (value === "" || value === null) {
		return null;
	}
	const parsed = typeof value === "number" ? value : Number.parseInt(value, 10);
	return Number.isFinite(parsed) ? parsed : null;
}

/** The stored scalar rendered for the text cell. A string shows as itself; everything else as its JSON text. */
function readValue(value: unknown): string {
	if (value === undefined || value === null) {
		return "";
	}
	return typeof value === "string" ? value : JSON.stringify(value);
}

/**
 * The text cell back into the scalar JSON the wire carries. `true`, `42` and `null` become themselves — the server
 * compares by JSON kind, so storing them as text makes the edge dead with nothing logged — and anything else stays the
 * string it was typed as, because a decision token is a string and quoting it would be noise.
 *
 * ponytail: a stored STRING that looks like a number turns into a number if the operator edits that one cell. The
 * lossless alternative is showing every string quoted, which makes the ordinary case (`Approve`) read as `"Approve"`.
 */
function parseConditionValue(text: string): unknown {
	try {
		const parsed: unknown = JSON.parse(text);
		return parsed === null || typeof parsed === "boolean" || typeof parsed === "number" ? parsed : text;
	} catch {
		return text;
	}
}
