import { Alert, Badge, Button, Group, Loader, ScrollArea, Stack, Tabs, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { GraphWorkflowDecisionPanel } from "@/features/graphWorkflows/components/GraphWorkflowDecisionPanel";
import { GraphWorkflowNodeStatusBadge } from "@/features/graphWorkflows/components/GraphWorkflowStatusBadge";
import {
	type GraphWorkflowDecisionKind,
	type GraphWorkflowNodeKind,
	narrowGraphWorkflowFailureClass,
	narrowGraphWorkflowNodeKind,
	narrowGraphWorkflowNodeRunStatus,
	narrowGraphWorkflowRunStatus,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { useGraphWorkflowNodeRun } from "@/features/graphWorkflows/queries/useGraphWorkflows";

export interface GraphWorkflowNodePanelProps {
	readonly runId: string;
	readonly nodeKey: string;
	/** Only for the currency line: a node that failed stays failed after the run has moved past it. */
	readonly runStatus: string | undefined;
	/** The Pause node's config, read off the definition graph by the page. Absent for every other kind. */
	readonly pauseConfig?: {
		readonly prompt: string;
		readonly allowedDecisions: readonly GraphWorkflowDecisionKind[];
		readonly requireComment: boolean;
	};
	readonly onClose: () => void;
}

function prettyJson(value: unknown): string {
	if (value === undefined || value === null) {
		return "";
	}
	return typeof value === "string" ? value : JSON.stringify(value, null, 2);
}

/** The `{ status, attempt, branch?, output }` envelope every node run's output document carries. */
function envelopeOutput(output: unknown): unknown {
	if (output === null || typeof output !== "object" || Array.isArray(output)) {
		return undefined;
	}
	return (output as { output?: unknown }).output;
}

function field(value: unknown, name: string): unknown {
	if (value === null || typeof value !== "object" || Array.isArray(value)) {
		return undefined;
	}
	return (value as Record<string, unknown>)[name];
}

function timestamp(value: number | null | undefined): string | undefined {
	return value == null ? undefined : new Date(value).toLocaleString();
}

/**
 * What a structural kind's output actually MEANS, which the document alone does not say. A Condition and a Parallel
 * node pass their input's output through verbatim, so an output identical to the predecessor's is correct rather than
 * a bug; a Join emits a map keyed by the node each branch came from. Without this line an operator reads the same
 * document twice and looks for the difference.
 */
function passThroughKey(kind: GraphWorkflowNodeKind): string | undefined {
	if (kind === "Condition" || kind === "Parallel") {
		return "passThrough";
	}
	return kind === "Join" ? "join" : undefined;
}

/**
 * The read-only pane for one node run: its header facts, the decision controls when the runtime is asking, and the
 * three documents (input, output, error) as pretty JSON.
 *
 * It owns its own query, the way `DevWorkflowNodePanel` does — the run payload carries the node ROWS but not their
 * documents, so the panel is the only thing that needs the detail read and the only thing that should pay for it.
 */
export function GraphWorkflowNodePanel({ runId, nodeKey, runStatus, pauseConfig, onClose }: GraphWorkflowNodePanelProps) {
	const { t } = useTranslation();
	const { data: nodeRun, isPending, error } = useGraphWorkflowNodeRun(runId, nodeKey);
	const [tab, setTab] = useState<string | null>("output");

	if (isPending) {
		return <Loader size="sm" data-testid="graph-workflow-node-panel-loading" />;
	}
	if (error || !nodeRun) {
		return (
			<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="graph-workflow-node-panel-error">
				{apiErrorMessage(error, t("pages.graphWorkflows.nodePanel.loadFailed", "This node could not be loaded."))}
			</Alert>
		);
	}

	const status = narrowGraphWorkflowNodeRunStatus(nodeRun.status);
	const kind = narrowGraphWorkflowNodeKind(nodeRun.kind);
	const failureClass = nodeRun.failureClass && nodeRun.failureClass !== "None" ? narrowGraphWorkflowFailureClass(nodeRun.failureClass) : undefined;
	const inner = envelopeOutput(nodeRun.output);
	const passThrough = passThroughKey(kind);
	// Two axes, deliberately: a node that failed stays failed once the run has routed around it and completed.
	const runMovedOn = status === "Failed" && narrowGraphWorkflowRunStatus(runStatus) !== "Failed";

	return (
		<ScrollArea h="100%" data-testid="graph-workflow-node-panel">
			<Stack gap="md" pr="xs">
				<Button size="xs" variant="subtle" onClick={onClose} data-testid="graph-workflow-node-panel-close">
					{t("pages.graphWorkflows.nodePanel.close", "Close")}
				</Button>

				<SectionCard gap="xs">
					<Group gap="xs" wrap="wrap">
						<Text fw={600} style={{ flex: 1, minWidth: 0 }} lineClamp={2} data-testid="graph-workflow-node-panel-key">
							{nodeRun.nodeKey}
						</Text>
						<GraphWorkflowNodeStatusBadge status={nodeRun.status} data-testid="graph-workflow-node-panel-status" />
					</Group>
					<Group gap={4} wrap="wrap">
						<Badge size="xs" variant="light" color="gray" data-testid="graph-workflow-node-panel-kind">
							{t(`pages.graphWorkflows.nodeKind.${kind}`, kind)}
						</Badge>
						<Text size="xs" c="dimmed" data-testid="graph-workflow-node-panel-attempt">
							{t("pages.graphWorkflows.nodePanel.attempt", "attempt {{attempt}}", { attempt: nodeRun.attempt ?? 1 })}
						</Text>
						{timestamp(nodeRun.startedAtUtc) ? (
							<Text size="xs" c="dimmed" data-testid="graph-workflow-node-panel-started">
								{t("pages.graphWorkflows.nodePanel.started", "started {{when}}", { when: timestamp(nodeRun.startedAtUtc) })}
							</Text>
						) : null}
						{timestamp(nodeRun.completedAtUtc) ? (
							<Text size="xs" c="dimmed" data-testid="graph-workflow-node-panel-completed">
								{t("pages.graphWorkflows.nodePanel.completed", "finished {{when}}", { when: timestamp(nodeRun.completedAtUtc) })}
							</Text>
						) : null}
					</Group>
				</SectionCard>

				{failureClass ? (
					<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="graph-workflow-node-panel-failure">
						<Stack gap={4}>
							<Text size="sm">{t(`pages.graphWorkflows.failureClass.${failureClass}`, failureClass)}</Text>
							{runMovedOn ? (
								<Text size="xs" c="dimmed" data-testid="graph-workflow-node-panel-moved-on">
									{t(
										"pages.graphWorkflows.nodePanel.runMovedOn",
										"The run carried on after this node failed — this is what this node did, not what the run ended up doing.",
									)}
								</Text>
							) : null}
						</Stack>
					</Alert>
				) : null}

				{nodeRun.pendingDecisionKind ? (
					<GraphWorkflowDecisionPanel
						runId={runId}
						nodeRun={nodeRun}
						allowedDecisions={pauseConfig?.allowedDecisions ?? []}
						requireComment={pauseConfig?.requireComment ?? false}
						prompt={pauseConfig?.prompt}
					/>
				) : null}

				<Tabs value={tab} onChange={setTab} data-testid="graph-workflow-node-panel-tabs">
					<Tabs.List>
						<Tabs.Tab value="input" data-testid="graph-workflow-node-panel-tab-input">
							{t("pages.graphWorkflows.nodePanel.tabInput", "Input")}
						</Tabs.Tab>
						<Tabs.Tab value="output" data-testid="graph-workflow-node-panel-tab-output">
							{t("pages.graphWorkflows.nodePanel.tabOutput", "Output")}
						</Tabs.Tab>
						<Tabs.Tab value="error" data-testid="graph-workflow-node-panel-tab-error">
							{t("pages.graphWorkflows.nodePanel.tabError", "Error")}
						</Tabs.Tab>
					</Tabs.List>

					<Tabs.Panel value="input" pt="xs">
						<Document value={nodeRun.input} testId="graph-workflow-node-panel-input" />
					</Tabs.Panel>

					<Tabs.Panel value="output" pt="xs">
						<Stack gap="xs">
							{/* An Agent's answer is prose, and reading it out of a JSON blob is the difference between a
							    surface an operator uses and one they only debug with. The raw document stays below it. */}
							{kind === "Agent" && typeof field(inner, "text") === "string" ? (
								<Text size="sm" style={{ whiteSpace: "pre-wrap" }} data-testid="graph-workflow-node-panel-agent-text">
									{field(inner, "text") as string}
								</Text>
							) : null}
							{/* A tool answers with an object or an array when it has structure and a bare string when it
							    does not; both are the tool's result, so both are rendered as one. */}
							{kind === "Tool" && field(inner, "result") !== undefined ? (
								<Document value={field(inner, "result")} testId="graph-workflow-node-panel-tool-result" />
							) : null}
							{kind === "Pause" && inner !== undefined ? (
								<Stack gap={2} data-testid="graph-workflow-node-panel-pause">
									<Text size="sm">
										{t("pages.graphWorkflows.nodePanel.pauseDecision", "Answered {{decision}}", {
											decision: String(field(inner, "decision") ?? ""),
										})}
									</Text>
									{typeof field(inner, "comment") === "string" ? (
										<Text size="xs" c="dimmed" style={{ whiteSpace: "pre-wrap" }}>
											{field(inner, "comment") as string}
										</Text>
									) : null}
								</Stack>
							) : null}
							{passThrough ? (
								<Text size="xs" c="dimmed" data-testid="graph-workflow-node-panel-pass-through">
									{passThrough === "passThrough"
										? t(
												"pages.graphWorkflows.nodePanel.passThrough",
												"A Condition and a Parallel node hand their input's output on unchanged, so an output that matches the node before it is correct.",
											)
										: t(
												"pages.graphWorkflows.nodePanel.joinOutput",
												"A Join node emits one map, keyed by the node each incoming branch came from.",
											)}
								</Text>
							) : null}
							<Document value={nodeRun.output} testId="graph-workflow-node-panel-output" />
						</Stack>
					</Tabs.Panel>

					<Tabs.Panel value="error" pt="xs">
						<Document value={nodeRun.error} testId="graph-workflow-node-panel-error-doc" />
					</Tabs.Panel>
				</Tabs>
			</Stack>
		</ScrollArea>
	);
}

/** One document as read-only pretty JSON, or a line saying the runtime recorded none. */
function Document({ value, testId }: { readonly value: unknown; readonly testId: string }) {
	const { t } = useTranslation();
	const text = prettyJson(value);
	if (text.length === 0) {
		return (
			<Text size="xs" c="dimmed" data-testid={`${testId}-empty`}>
				{t("pages.graphWorkflows.nodePanel.emptyDocument", "The runtime recorded nothing here.")}
			</Text>
		);
	}
	return <CodeEditor value={text} language="json" readOnly={true} height={220} aria-label={testId} data-testid={testId} />;
}
