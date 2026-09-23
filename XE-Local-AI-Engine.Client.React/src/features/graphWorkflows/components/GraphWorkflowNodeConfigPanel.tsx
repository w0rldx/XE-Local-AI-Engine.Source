// The selected node's whole configuration, as a plain `Stack` the page drops into whatever container it wants (a side
// pane on a wide viewport, a `Drawer` on a narrow one). PROP-DRIVEN: it owns no query and no graph state, so the
// editor hook stays the single source of truth for the canvas and this file can be tested without a network.
//
// Two validation mechanisms meet here, on purpose. The STRUCTURAL issues (`GraphWorkflowGraphIssue`) are the graph's
// own rules and the page filters them to this node; they render at the top and gate Save elsewhere. The Zod schemas
// from `GraphWorkflowValidation` run per field and only produce a message — and only once the field has been touched,
// so a half-typed JSON document is not shouted at on its second character.
//
// The Agent and Tool bodies live in `config/` for size alone; every other kind is a handful of controls and stays here.

import { Button, Checkbox, Group, NumberInput, Select, Stack, Switch, Text, Textarea, TextInput } from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { ZodType } from "zod";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { GraphWorkflowAgentConfigForm } from "@/features/graphWorkflows/components/config/GraphWorkflowAgentConfigForm";
import { GraphWorkflowDecisionModelConfigForm } from "@/features/graphWorkflows/components/config/GraphWorkflowDecisionModelConfigForm";
import { GraphWorkflowJsonField } from "@/features/graphWorkflows/components/config/GraphWorkflowJsonField";
import { GraphWorkflowLlmCallConfigForm } from "@/features/graphWorkflows/components/config/GraphWorkflowLlmCallConfigForm";
import { GraphWorkflowToolConfigForm } from "@/features/graphWorkflows/components/config/GraphWorkflowToolConfigForm";
import {
	type GraphWorkflowCanvasNodeData,
	type GraphWorkflowGraphSettings,
	graphWorkflowChatDefaults,
	standardGraphSettings,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import {
	GRAPH_WORKFLOW_KEY_PATTERN,
	type GraphWorkflowDecisionKind,
	type GraphWorkflowNodeKind,
	type GraphWorkflowToolResponse,
	graphWorkflowDefaultMaxAttempts,
	graphWorkflowJoinPolicies,
	graphWorkflowPauseDecisionKinds,
	narrowGraphWorkflowJoinPolicy,
	toGraphWorkflowDecisionKinds,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import {
	agentConfigSchema,
	chatInputConfigSchema,
	conditionConfigSchema,
	decisionModelConfigSchema,
	endConfigSchema,
	type GraphWorkflowGraphIssue,
	llmCallConfigSchema,
	nodeCommonSchema,
	pauseConfigSchema,
	startConfigSchema,
	toolConfigSchema,
} from "@/features/graphWorkflows/models/GraphWorkflowValidation";

export interface GraphWorkflowNodeConfigPanelProps {
	readonly node: GraphWorkflowCanvasNodeData;
	/** Only the issues whose `subject` is this node's key — the page filters. */
	readonly issues: readonly GraphWorkflowGraphIssue[];
	readonly onChange: (patch: Partial<GraphWorkflowCanvasNodeData>) => void;
	/** The page forwards the editor's `renameNode`; the refusal is rendered on the key field. */
	readonly onRename: (to: string) => "ok" | "collision" | "invalid";
	readonly onRemove: () => void;
	readonly tools: readonly GraphWorkflowToolResponse[];
	readonly agentOptions: readonly { readonly value: string; readonly label: string }[];
	readonly modelOptions: readonly { readonly value: string; readonly label: string }[];
	readonly llmModelOptions?: readonly { readonly value: string; readonly label: string }[];
	/** The graph's `kind` and `chat` block: they decide which chat switches apply and what End publishes by default. */
	readonly graphSettings?: GraphWorkflowGraphSettings;
	readonly readOnly?: boolean;
}

/** The per-field Zod schema for a kind, on top of `nodeCommonSchema`. `Parallel` and `Join` configure nothing. */
function configSchemaFor(kind: GraphWorkflowNodeKind): ZodType | undefined {
	switch (kind) {
		case "Start":
			return startConfigSchema;
		case "Agent":
			return agentConfigSchema;
		case "LlmCall":
			return llmCallConfigSchema;
		case "Tool":
			return toolConfigSchema;
		case "Condition":
			return conditionConfigSchema;
		case "Pause":
			return pauseConfigSchema;
		case "End":
			return endConfigSchema;
		case "ChatInput":
			return chatInputConfigSchema;
		case "DecisionModel":
			return decisionModelConfigSchema;
		default:
			return undefined;
	}
}

/** Zod issues as `field → message`, where the message is already a full i18n key (`GraphWorkflowValidation`'s rule). */
function messagesByField(node: GraphWorkflowCanvasNodeData): Readonly<Record<string, string>> {
	const messages: Record<string, string> = {};
	const schemas: readonly (ZodType | undefined)[] = [nodeCommonSchema, configSchemaFor(node.kind)];
	for (const schema of schemas) {
		const result = schema?.safeParse(node);
		if (result === undefined || result.success) {
			continue;
		}
		for (const issue of result.error.issues) {
			const field = String(issue.path[0] ?? "");
			messages[field] ??= issue.message;
		}
	}
	return messages;
}

export function GraphWorkflowNodeConfigPanel({
	node,
	issues,
	onChange,
	onRename,
	onRemove,
	tools,
	agentOptions,
	modelOptions,
	llmModelOptions = modelOptions,
	graphSettings = standardGraphSettings,
	readOnly = false,
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

	const messages = useMemo(() => messagesByField(node), [node]);
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

	// One inline switch rather than eight exported components: the four short bodies close over `t`, `errorFor`,
	// `touch` and `onChange` anyway, and only Agent and Tool were long enough to be worth their own file.
	const body = (() => {
		switch (node.kind) {
			case "Start":
				return (
					<>
						<GraphWorkflowJsonField
							label={t("pages.graphWorkflows.config.inputSchema", "Input schema (JSON)")}
							value={node.inputSchema}
							error={errorFor("inputSchema")}
							readOnly={readOnly}
							onChange={(next) => {
								touch("inputSchema");
								onChange({ inputSchema: next });
							}}
							data-testid="gw-node-config-input-schema"
						/>
						<GraphWorkflowJsonField
							label={t("pages.graphWorkflows.config.defaultInput", "Default input (JSON)")}
							value={node.defaultInput}
							error={errorFor("defaultInput")}
							readOnly={readOnly}
							onChange={(next) => {
								touch("defaultInput");
								onChange({ defaultInput: next });
							}}
							data-testid="gw-node-config-default-input"
						/>
					</>
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
						readOnly={readOnly}
					/>
				);
			case "Condition":
				return (
					<TextInput
						label={t("pages.graphWorkflows.config.path", "Path")}
						description={t(
							"pages.graphWorkflows.config.conditionPathHelp",
							"Outgoing edges with no path of their own compare this one.",
						)}
						placeholder="output.json.status"
						value={node.path ?? ""}
						disabled={readOnly}
						error={errorFor("path")}
						onBlur={() => touch("path")}
						onChange={(event) => onChange({ path: event.currentTarget.value })}
						data-testid="gw-node-config-path"
					/>
				);
			case "Pause":
				return (
					<>
						<Textarea
							label={t("pages.graphWorkflows.config.prompt", "Question")}
							value={node.prompt}
							autosize={true}
							minRows={2}
							maxRows={8}
							disabled={readOnly}
							error={errorFor("prompt")}
							onBlur={() => touch("prompt")}
							onChange={(event) => onChange({ prompt: event.currentTarget.value })}
							data-testid="gw-node-config-prompt"
						/>
						<Checkbox.Group
							label={t("pages.graphWorkflows.config.allowedDecisions", "Offered decisions")}
							value={[...node.allowedDecisions]}
							error={errorFor("allowedDecisions")}
							onChange={(values) => {
								touch("allowedDecisions");
								onChange({ allowedDecisions: toGraphWorkflowDecisionKinds(values) });
							}}
							data-testid="gw-node-config-decisions"
						>
							<Group gap="md" mt={6}>
								{graphWorkflowPauseDecisionKinds.map((decision: GraphWorkflowDecisionKind) => (
									<Checkbox
										key={decision}
										value={decision}
										disabled={readOnly}
										label={t(`pages.graphWorkflows.decision.${decision}`, decision)}
										data-testid={`gw-node-config-decision-${decision}`}
									/>
								))}
							</Group>
						</Checkbox.Group>
						<Switch
							label={t("pages.graphWorkflows.config.requireComment", "A comment is required")}
							checked={node.requireComment}
							disabled={readOnly}
							onChange={(event) => onChange({ requireComment: event.currentTarget.checked })}
							data-testid="gw-node-config-require-comment"
						/>
					</>
				);
			case "End":
				return (
					<>
						<TextInput
							label={t("pages.graphWorkflows.config.outcome", "Outcome")}
							value={node.outcome}
							disabled={readOnly}
							error={errorFor("outcome")}
							onBlur={() => touch("outcome")}
							onChange={(event) => onChange({ outcome: event.currentTarget.value })}
							data-testid="gw-node-config-outcome"
						/>
						<TextInput
							label={t("pages.graphWorkflows.config.resultPath", "Result path")}
							placeholder={t("pages.graphWorkflows.config.resultPathPlaceholder", "The whole document")}
							value={node.resultPath ?? ""}
							disabled={readOnly}
							error={errorFor("resultPath")}
							onBlur={() => touch("resultPath")}
							onChange={(event) => onChange({ resultPath: event.currentTarget.value })}
							data-testid="gw-node-config-result-path"
						/>
					</>
				);
			case "ChatInput":
				return (
					<Textarea
						label={t("pages.graphWorkflows.config.chatInputPrompt", "What to ask the user")}
						description={t(
							"pages.graphWorkflows.config.chatInputPromptHelp",
							"The run waits here until the user answers in the conversation.",
						)}
						value={node.prompt}
						autosize={true}
						minRows={2}
						maxRows={8}
						disabled={readOnly}
						error={errorFor("prompt")}
						onBlur={() => touch("prompt")}
						onChange={(event) => onChange({ prompt: event.currentTarget.value })}
						data-testid="gw-node-config-chat-prompt"
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
			<ChatSwitches node={node} graphSettings={graphSettings} onChange={onChange} readOnly={readOnly} />
		</Stack>
	);
}

/**
 * `publishToChat` (Agent, LlmCall, End) and `includeAttachments` (Agent, LlmCall). Shown on a Chat graph, and on any
 * graph where the flag is already on — the parser refuses that outside a Chat graph, so the operator needs the switch
 * to turn it off. An absent flag shows the parser's default: `true` for an End in a Chat graph, `false` otherwise.
 */
function ChatSwitches({
	node,
	graphSettings,
	onChange,
	readOnly,
}: {
	readonly node: GraphWorkflowCanvasNodeData;
	readonly graphSettings: GraphWorkflowGraphSettings;
	readonly onChange: (patch: Partial<GraphWorkflowCanvasNodeData>) => void;
	readonly readOnly: boolean;
}) {
	const { t } = useTranslation();
	if (node.kind !== "Agent" && node.kind !== "LlmCall" && node.kind !== "End") {
		return null;
	}
	const isChat = graphSettings.kind === "Chat";
	const acceptsAttachments = isChat && (graphSettings.chat?.acceptsAttachments ?? graphWorkflowChatDefaults.acceptsAttachments);
	const publishDefault = node.kind === "End" && isChat;
	const publish = node.publishToChat ?? publishDefault;
	const include = node.kind === "End" ? undefined : node.includeAttachments;
	return (
		<>
			{isChat || node.publishToChat === true ? (
				<Switch
					label={t("pages.graphWorkflows.config.publishToChat", "Post the result to the chat")}
					checked={publish}
					disabled={readOnly}
					// The parser default is written as ABSENT, so an End toggled off and on again saves byte for byte.
					onChange={(event) =>
						onChange({ publishToChat: event.currentTarget.checked === publishDefault ? undefined : event.currentTarget.checked })
					}
					data-testid="gw-node-config-publish-to-chat"
				/>
			) : null}
			{node.kind !== "End" && (isChat || include === true) ? (
				<Switch
					label={t("pages.graphWorkflows.config.includeAttachments", "Include the chat attachments")}
					description={
						acceptsAttachments
							? undefined
							: t(
									"pages.graphWorkflows.config.includeAttachmentsDisabled",
									"Turn on “Accepts attachments” in the workflow settings first.",
								)
					}
					checked={include ?? false}
					// Enabled while ON even when the graph refuses attachments, so a refused flag can still be turned off.
					disabled={readOnly || (!acceptsAttachments && include !== true)}
					onChange={(event) => onChange({ includeAttachments: event.currentTarget.checked ? true : undefined })}
					data-testid="gw-node-config-include-attachments"
				/>
			) : null}
		</>
	);
}
