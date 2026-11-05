// The selected node's whole configuration, as a plain `Stack` the page drops into whatever container it wants (a side
// pane on a wide viewport, a `Drawer` on a narrow one). PROP-DRIVEN: it owns no query and no graph state, so the
// editor hook stays the single source of truth for the canvas and this file can be tested without a network.
//
// Two validation mechanisms meet here, on purpose. The STRUCTURAL issues (`GraphWorkflowGraphIssue`) are the graph's
// own rules and the page filters them to this node; they render at the top and gate Save elsewhere. The Zod schemas
// from `GraphWorkflowConfigSchemas` run per field and only produce a message — and only once the field has been touched,
// so a half-typed JSON document is not shouted at on its second character.
//
// Every kind with controls has its body in `config/`, taking the same core props; this file is the shell and the one
// switch.

import { Button, Group, NumberInput, Select, Stack, Text, TextInput } from "@mantine/core";
import { IconSparkles, IconTrash } from "@tabler/icons-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import {
	GraphWorkflowAgentConfigForm,
	type GraphWorkflowAgentOption,
	type GraphWorkflowModelOption,
} from "@/features/graphWorkflows/components/config/GraphWorkflowAgentConfigForm";
import { GraphWorkflowChatSwitches } from "@/features/graphWorkflows/components/config/GraphWorkflowChatSwitches";
import { GraphWorkflowDecisionModelConfigForm } from "@/features/graphWorkflows/components/config/GraphWorkflowDecisionModelConfigForm";
import { GraphWorkflowLlmCallConfigForm } from "@/features/graphWorkflows/components/config/GraphWorkflowLlmCallConfigForm";
import {
	GraphWorkflowChatInputConfigForm,
	GraphWorkflowConditionConfigForm,
	GraphWorkflowEndConfigForm,
	GraphWorkflowPauseConfigForm,
	GraphWorkflowStartConfigForm,
} from "@/features/graphWorkflows/components/config/GraphWorkflowSimpleConfigForms";
import { GraphWorkflowToolConfigForm } from "@/features/graphWorkflows/components/config/GraphWorkflowToolConfigForm";
import {
	type GraphWorkflowCanvasNodeData,
	type GraphWorkflowGraphSettings,
	standardGraphSettings,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { graphWorkflowNodeFieldMessages } from "@/features/graphWorkflows/models/GraphWorkflowConfigSchemas";
import {
	GRAPH_WORKFLOW_KEY_PATTERN,
	type GraphWorkflowToolResponse,
	graphWorkflowDefaultMaxAttempts,
	graphWorkflowJoinPolicies,
	narrowGraphWorkflowJoinPolicy,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import type { GraphWorkflowGraphIssue } from "@/features/graphWorkflows/models/GraphWorkflowValidation";

export interface GraphWorkflowNodeConfigPanelProps {
	readonly node: GraphWorkflowCanvasNodeData;
	/** Only the issues whose `subject` is this node's key — the page filters. */
	readonly issues: readonly GraphWorkflowGraphIssue[];
	readonly onChange: (patch: Partial<GraphWorkflowCanvasNodeData>) => void;
	/** The page forwards the editor's `renameNode`; the refusal is rendered on the key field. */
	readonly onRename: (to: string) => "ok" | "collision" | "invalid";
	readonly onRemove: () => void;
	readonly tools: readonly GraphWorkflowToolResponse[];
	/** The open definition's `allowsActions`: without it an action tool is offered disabled, with the server's reason. */
	readonly allowsActions?: boolean;
	readonly agentOptions: readonly GraphWorkflowAgentOption[];
	readonly modelOptions: readonly GraphWorkflowModelOption[];
	readonly llmModelOptions?: readonly GraphWorkflowModelOption[];
	/** Installed `Decision`-kind models, for a `systemone` DecisionModel node. */
	readonly decisionModelOptions?: readonly GraphWorkflowModelOption[];
	/** Whether the LLM and Decision model queries succeeded; the DecisionModel form only clears a model against a loaded list. */
	readonly llmModelOptionsReady?: boolean;
	readonly decisionModelOptionsReady?: boolean;
	/** The graph's `kind` and `chat` block: they decide which chat switches apply and what End publishes by default. */
	readonly graphSettings?: GraphWorkflowGraphSettings;
	readonly readOnly?: boolean;
	/** Opens the Assistant tab about this node. Absent where there is no assistant, such as the run view. */
	readonly onAskAssistant?: () => void;
}

export function GraphWorkflowNodeConfigPanel({
	node,
	issues,
	onChange,
	onRename,
	onRemove,
	tools,
	allowsActions = false,
	agentOptions,
	modelOptions,
	llmModelOptions = modelOptions,
	decisionModelOptions = [],
	llmModelOptionsReady = true,
	decisionModelOptionsReady = true,
	graphSettings = standardGraphSettings,
	readOnly = false,
	onAskAssistant,
}: GraphWorkflowNodeConfigPanelProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const [keyDraft, setKeyDraft] = useState(node.key);
	const [keyMessage, setKeyMessage] = useState<string | undefined>(undefined);
	const [touched, setTouched] = useState<readonly string[]>([]);
	// A bounded Mantine NumberInput fires an `onChange` of its own on mount (agent-knowledge §5), which would turn an
	// unset `maxAttempts` into the minimum before the operator has typed anything. Effects run child-first, so this is
	// still false while that spurious change fires and true by the time a real edit can happen. NOT covered by a test:
	// Mantine does not fire that change under jsdom, so any test of it would pass with the guard deleted.
	const ready = useRef(false);
	useEffect(() => {
		ready.current = true;
	}, []);

	// The key field is a DRAFT: the rename is a graph operation applied on blur or Enter, not on every keystroke.
	useEffect(() => {
		setKeyDraft(node.key);
		setKeyMessage(undefined);
		setTouched([]);
	}, [node.key]);

	const messages = useMemo(() => graphWorkflowNodeFieldMessages(node), [node]);
	const touch = (field: string): void => setTouched((current) => (current.includes(field) ? current : [...current, field]));
	const errorFor = (field: string): string | undefined => {
		const message = messages[field];
		return message !== undefined && touched.includes(field) ? t(message) : undefined;
	};
	const keyInvalidMessage = t(
		"pages.graphWorkflows.form.key.invalid",
		"Use letters, digits, hyphens and underscores, up to 64 characters.",
	);

	const commitKey = (): void => {
		if (keyDraft === node.key) {
			return;
		}
		const result = onRename(keyDraft);
		setKeyMessage(
			result === "ok"
				? undefined
				: result === "collision"
					? t("pages.graphWorkflows.form.key.collision", "That key already belongs to another node or edge.")
					: keyInvalidMessage,
		);
	};

	const handleRemove = async (): Promise<void> => {
		const confirmed = await confirm({
			title: t("pages.graphWorkflows.config.removeTitle", "Delete this node?"),
			description: t(
				"pages.graphWorkflows.config.removeDescription",
				"Its configuration and every edge attached to it go with it. Runs that already started are untouched.",
			),
			confirmationText: t("pages.graphWorkflows.config.removeConfirm", "Delete"),
			cancellationText: t("common.cancel", "Cancel"),
		});
		if (confirmed) {
			onRemove();
		}
	};

	const body = (() => {
		switch (node.kind) {
			case "Start":
				return (
					<GraphWorkflowStartConfigForm node={node} onChange={onChange} errorFor={errorFor} onTouch={touch} readOnly={readOnly} />
				);
			case "Agent":
				return (
					<GraphWorkflowAgentConfigForm
						node={node}
						onChange={onChange}
						errorFor={errorFor}
						onTouch={touch}
						agentOptions={agentOptions}
						modelOptions={modelOptions}
						readOnly={readOnly}
					/>
				);
			case "LlmCall":
				return (
					<GraphWorkflowLlmCallConfigForm
						node={node}
						onChange={onChange}
						errorFor={errorFor}
						onTouch={touch}
						modelOptions={llmModelOptions}
						readOnly={readOnly}
					/>
				);
			case "Tool":
				return (
					<GraphWorkflowToolConfigForm
						node={node}
						onChange={onChange}
						errorFor={errorFor}
						onTouch={touch}
						tools={tools}
						allowsActions={allowsActions}
						readOnly={readOnly}
					/>
				);
			case "Condition":
				return (
					<GraphWorkflowConditionConfigForm
						node={node}
						onChange={onChange}
						errorFor={errorFor}
						onTouch={touch}
						readOnly={readOnly}
					/>
				);
			case "Pause":
				return (
					<GraphWorkflowPauseConfigForm node={node} onChange={onChange} errorFor={errorFor} onTouch={touch} readOnly={readOnly} />
				);
			case "End":
				return (
					<GraphWorkflowEndConfigForm node={node} onChange={onChange} errorFor={errorFor} onTouch={touch} readOnly={readOnly} />
				);
			case "ChatInput":
				return (
					<GraphWorkflowChatInputConfigForm
						node={node}
						onChange={onChange}
						errorFor={errorFor}
						onTouch={touch}
						readOnly={readOnly}
					/>
				);
			case "DecisionModel":
				return (
					<GraphWorkflowDecisionModelConfigForm
						node={node}
						onChange={onChange}
						errorFor={errorFor}
						onTouch={touch}
						modelOptions={llmModelOptions}
						decisionModelOptions={decisionModelOptions}
						modelOptionsReady={llmModelOptionsReady}
						decisionModelOptionsReady={decisionModelOptionsReady}
						readOnly={readOnly}
					/>
				);
			// `default` IS the Parallel/Join case: neither configures anything, so the panel says what the node does
			// instead of offering controls it has none of.
			default:
				return (
					<Text size="sm" c="dimmed" data-testid="gw-node-config-passthrough">
						{node.kind === "Parallel"
							? t("pages.graphWorkflows.config.parallelHelp", "A Parallel node hands its input to every outgoing edge.")
							: t(
									"pages.graphWorkflows.config.joinHelp",
									"A Join node collects its incoming branches into a map keyed by source node.",
								)}
					</Text>
				);
		}
	})();

	return (
		<Stack gap="sm" data-testid="gw-node-config">
			<Group justify="space-between" wrap="nowrap" align="center">
				<Text fw={600} data-testid="gw-node-config-kind">
					{t(`pages.graphWorkflows.nodeKind.${node.kind}`, node.kind)}
				</Text>
				{onAskAssistant === undefined ? null : (
					<Button
						size="xs"
						variant="subtle"
						leftSection={<IconSparkles size={14} />}
						onClick={onAskAssistant}
						data-testid="gw-node-config-ask-ai"
					>
						{t("pages.graphWorkflows.designer.askAboutNode", "Ask AI")}
					</Button>
				)}
				<Button
					size="xs"
					variant="light"
					color="red"
					leftSection={<IconTrash size={14} />}
					disabled={readOnly}
					onClick={() => {
						handleRemove().catch(() => undefined);
					}}
					data-testid="gw-node-config-remove"
				>
					{t("pages.graphWorkflows.config.remove", "Delete node")}
				</Button>
			</Group>

			{issues.length > 0 ? (
				<InlineErrorAlert
					variant="light"
					data-testid="gw-node-config-issues"
					message={
						<Stack gap={4}>
							{issues.map((issue) => (
								<Text key={`${issue.rule}:${issue.subject ?? ""}`} size="sm">
									{issue.message ??
										t(`pages.graphWorkflows.definition.issues.${issue.rule}`, issue.rule, { subject: issue.subject ?? "" })}
								</Text>
							))}
						</Stack>
					}
				/>
			) : null}

			<TextInput
				label={t("pages.graphWorkflows.config.key", "Key")}
				description={t(
					"pages.graphWorkflows.config.keyHelp",
					"Letters, digits, hyphens and underscores. Nodes and edges share one set of keys.",
				)}
				value={keyDraft}
				disabled={readOnly}
				error={keyMessage}
				onChange={(event) => {
					const next = event.currentTarget.value;
					setKeyDraft(next);
					setKeyMessage(GRAPH_WORKFLOW_KEY_PATTERN.test(next) ? undefined : keyInvalidMessage);
				}}
				onBlur={commitKey}
				onKeyDown={(event) => {
					if (event.key === "Enter") {
						commitKey();
					}
				}}
				data-testid="gw-node-config-key"
			/>
			<TextInput
				label={t("pages.graphWorkflows.config.label", "Label")}
				placeholder={t("pages.graphWorkflows.config.labelPlaceholder", "Shown on the card")}
				value={node.label}
				disabled={readOnly}
				onChange={(event) => onChange({ label: event.currentTarget.value })}
				data-testid="gw-node-config-label"
			/>
			<Group grow={true} align="flex-start" wrap="wrap">
				<Select
					label={t("pages.graphWorkflows.config.joinPolicy", "When more than one edge arrives")}
					data={graphWorkflowJoinPolicies.map((policy) => ({
						value: policy,
						label: t(`pages.graphWorkflows.joinPolicy.${policy}`, policy),
					}))}
					value={narrowGraphWorkflowJoinPolicy(node.joinPolicy)}
					allowDeselect={false}
					disabled={readOnly}
					onChange={(value) => onChange({ joinPolicy: narrowGraphWorkflowJoinPolicy(value) })}
					data-testid="gw-node-config-join"
				/>
				<NumberInput
					label={t("pages.graphWorkflows.config.maxAttempts", "Max attempts")}
					// The kind's own default, shown rather than written: an unset field means "whatever the runtime uses".
					placeholder={String(graphWorkflowDefaultMaxAttempts(node.kind))}
					value={node.maxAttempts ?? ""}
					min={1}
					// The schema's own bound, and `clampBehavior="none"` so a stored value OUTSIDE it is never rewritten:
					// Mantine clamps on blur by default, which would silently turn a stored 200 into the maximum and dirty
					// the graph on a tab-through. The server has no upper bound at all, so the Zod message is what says the
					// value is out of range — the field does not get to edit it.
					max={100}
					clampBehavior="none"
					allowDecimal={false}
					disabled={readOnly}
					error={errorFor("maxAttempts")}
					onBlur={() => touch("maxAttempts")}
					onChange={(value) => {
						if (!ready.current) {
							return;
						}
						touch("maxAttempts");
						onChange({ maxAttempts: typeof value === "number" ? value : undefined });
					}}
					data-testid="gw-node-config-attempts"
				/>
				<NumberInput
					label={t("pages.graphWorkflows.config.timeoutSeconds", "Timeout (seconds)")}
					placeholder={t("pages.graphWorkflows.config.timeoutPlaceholder", "No timeout")}
					value={node.timeoutSeconds ?? ""}
					min={1}
					allowDecimal={false}
					disabled={readOnly}
					error={errorFor("timeoutSeconds")}
					onBlur={() => touch("timeoutSeconds")}
					onChange={(value) => {
						if (!ready.current) {
							return;
						}
						touch("timeoutSeconds");
						onChange({ timeoutSeconds: typeof value === "number" ? value : undefined });
					}}
					data-testid="gw-node-config-timeout"
				/>
			</Group>

			{body}
			<GraphWorkflowChatSwitches node={node} graphSettings={graphSettings} onChange={onChange} readOnly={readOnly} />
		</Stack>
	);
}
