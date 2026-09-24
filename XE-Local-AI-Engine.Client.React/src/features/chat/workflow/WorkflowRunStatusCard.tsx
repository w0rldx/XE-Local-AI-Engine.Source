import { Alert, Anchor, Button, CloseButton, Group, Paper, Stack, Text, Textarea } from "@mantine/core";
import { IconHandStop, IconPlayerStopFilled } from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { Fragment, type ReactNode, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import {
	activeWorkflowNode,
	formatWorkflowElapsed,
	isWaitingForWorkflowInput,
	steerableWorkflowNode,
	type WorkflowPath,
	workflowNodeGlyph,
	workflowNodeWithStatus,
} from "@/features/chat/workflow/ChatWorkflowModels";
import { GraphWorkflowRunStatusBadge } from "@/features/graphWorkflows/components/GraphWorkflowStatusBadge";
import {
	isTerminalGraphWorkflowRunStatus,
	narrowGraphWorkflowRunStatus,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

const ELAPSED_TICK_MS = 1_000;

interface WorkflowRunStatusCardProps {
	readonly runId: string;
	readonly definitionId: string;
	readonly workflowName: string;
	readonly status: string;
	readonly path: WorkflowPath;
	/** The failed node's error, when the run failed. */
	readonly failureReason?: string;
	readonly stopping: boolean;
	readonly onStop: () => void;
	/** Offered on a terminal run only: acknowledging it hides the card. */
	readonly onDismiss: () => void;
	/** The active node's live output (reasoning, phase, tokens). Rendered under the active line while the run is live. */
	readonly liveDetail?: ReactNode;
	/**
	 * The model the active node actually runs on, when known (its live stream, then its output document). Wins over the
	 * pinned config's `model`, which is `null` for a node that runs on the default.
	 */
	readonly activeModel?: string;
	/**
	 * Sends the operator's steering to a running Agent / LLM Call. Resolves once the server accepted it; rejects with an
	 * Error whose message is what the dialog shows (the draft stays). Absent ⇒ no Intervene.
	 */
	readonly onSteer?: (runId: string, nodeKey: string, message: string) => Promise<void>;
}

interface SteerTarget {
	readonly runId: string;
	readonly key: string;
	readonly label: string;
}

/**
 * The Intervene dialog. The run and node are captured at open, so a node that moves on (or a run that ends) mid-draft is
 * still the one addressed, and the server's refusal, not a silent close, is what the operator sees.
 */
function SteerDialog({
	target,
	onClose,
	onSteer,
}: {
	readonly target: SteerTarget | undefined;
	readonly onClose: () => void;
	readonly onSteer: (runId: string, nodeKey: string, message: string) => Promise<void>;
}) {
	const { t } = useTranslation();
	const [message, setMessage] = useState("");
	const [error, setError] = useState<string | undefined>();
	const [sending, setSending] = useState(false);
	const close = (): void => {
		setMessage("");
		setError(undefined);
		onClose();
	};
	const submit = (): void => {
		if (!target) {
			return;
		}
		setSending(true);
		setError(undefined);
		onSteer(target.runId, target.key, message.trim())
			.then(close)
			.catch((reason: unknown) => setError(reason instanceof Error ? reason.message : String(reason)))
			.finally(() => setSending(false));
	};
	return (
		<DialogShell
			opened={target !== undefined}
			onClose={close}
			size="md"
			enableFullScreenToggle={false}
			title={t("pages.chat.workflow.steer.title", "Steer {{node}}", { node: target?.label ?? "" })}
			data-testid="chat-workflow-steer-dialog"
			footer={
				<Group justify="flex-end">
					<Button variant="default" onClick={close}>
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						onClick={submit}
						loading={sending}
						disabled={message.trim().length === 0}
						data-testid="chat-workflow-steer-send"
					>
						{t("pages.chat.workflow.steer.send", "Send steering")}
					</Button>
				</Group>
			}
		>
			<Stack gap="xs">
				{error ? (
					<Alert color="orange" variant="light" data-testid="chat-workflow-steer-error">
						{error}
					</Alert>
				) : null}
				<Textarea
					label={t("pages.chat.workflow.steer.label", "What should the agent do differently?")}
					autosize={true}
					minRows={3}
					data-autofocus={true}
					value={message}
					onChange={(event) => setMessage(event.currentTarget.value)}
					data-testid="chat-workflow-steer-message"
				/>
			</Stack>
		</DialogShell>
	);
}

function useNow(active: boolean): number {
	const [now, setNow] = useState(() => Date.now());
	useEffect(() => {
		if (!active) {
			return;
		}
		// Re-read the clock at once: the value from mount may be minutes old by the time a node starts.
		setNow(Date.now());
		// real-timer: the elapsed readout is wall-clock by definition; nothing here is awaited by a test.
		const handle = setInterval(() => setNow(Date.now()), ELAPSED_TICK_MS);
		return () => clearInterval(handle);
	}, [active]);
	return now;
}

/** Above the composer while the conversation's newest bound run is live, or finished and not yet dismissed. */
export function WorkflowRunStatusCard({
	runId,
	definitionId,
	workflowName,
	status,
	path,
	failureReason,
	stopping,
	onStop,
	onDismiss,
	liveDetail,
	activeModel,
	onSteer,
}: WorkflowRunStatusCardProps) {
	const { t } = useTranslation();
	const narrowed = narrowGraphWorkflowRunStatus(status);
	const terminal = isTerminalGraphWorkflowRunStatus(narrowed);
	const active = terminal ? undefined : activeWorkflowNode(path);
	const now = useNow(active?.startedAtUtc != null);
	const steerable = active && narrowed !== "Cancelling" ? steerableWorkflowNode(path) : undefined;
	const [steerTarget, setSteerTarget] = useState<SteerTarget | undefined>();

	let terminalText: string | undefined;
	if (narrowed === "Completed") {
		terminalText = t("pages.chat.workflow.status.completed", "Completed");
	} else if (narrowed === "Cancelled") {
		const node = workflowNodeWithStatus(path, "Cancelled");
		terminalText = node
			? t("pages.chat.workflow.status.cancelledDuring", "Cancelled during {{node}}", { node: node.label })
			: t("pages.chat.workflow.status.cancelled", "Cancelled");
	} else if (narrowed === "Failed") {
		const node = workflowNodeWithStatus(path, "Failed");
		terminalText = t("pages.chat.workflow.status.failedAt", "Failed at {{node}}: {{reason}}", {
			node: node?.label ?? "—",
			reason: failureReason ?? t("pages.chat.workflow.status.unknownReason", "no reason was recorded"),
		});
	}

	return (
		<Paper withBorder={true} radius="md" p="xs" data-testid="chat-workflow-status-card">
			<Stack gap={6}>
				<Group justify="space-between" wrap="nowrap" gap="xs">
					<Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
						<Text size="sm" fw={700} lineClamp={1} data-testid="chat-workflow-status-name">
							{workflowName}
						</Text>
						<GraphWorkflowRunStatusBadge
							status={status}
							waitingForInput={isWaitingForWorkflowInput(path)}
							data-testid="chat-workflow-status-badge"
						/>
					</Group>
					<Group gap={4} wrap="nowrap">
						{/* `Link` directly, not `Anchor component={Link}`: the polymorphic prop erases the router's search typing. */}
						<Link to={nodeRoutePaths.graphWorkflows} search={{ runId, definitionId }} data-testid="chat-workflow-status-view">
							<Anchor component="span" size="xs">
								{t("pages.chat.workflow.status.view", "View workflow")}
							</Anchor>
						</Link>
						{terminal ? (
							<CloseButton
								size="sm"
								onClick={onDismiss}
								aria-label={t("pages.chat.workflow.status.dismiss", "Dismiss")}
								data-testid="chat-workflow-status-dismiss"
							/>
						) : (
							<>
								{onSteer && steerable ? (
									<Button
										size="compact-xs"
										variant="light"
										leftSection={<IconHandStop size={12} />}
										onClick={() => setSteerTarget({ runId, key: steerable.key, label: steerable.label })}
										data-testid="chat-workflow-status-intervene"
									>
										{t("pages.chat.workflow.status.intervene", "Intervene")}
									</Button>
								) : null}
								<Button
									size="compact-xs"
									color="red"
									variant="light"
									leftSection={<IconPlayerStopFilled size={12} />}
									loading={stopping}
									disabled={narrowed === "Cancelling"}
									onClick={onStop}
									data-testid="chat-workflow-status-stop"
								>
									{t("pages.chat.workflow.status.stop", "Stop")}
								</Button>
							</>
						)}
					</Group>
				</Group>
				{path.length > 0 ? (
					<Group gap={6} wrap="wrap" data-testid="chat-workflow-status-path">
						{path.map((rank, index) => (
							<Fragment key={rank.map((node) => node.key).join("|")}>
								{index > 0 ? (
									<Text size="xs" c="dimmed" aria-hidden={true}>
										›
									</Text>
								) : null}
								<Group gap={8} wrap="nowrap">
									{rank.map((node) => (
										<Text
											key={node.key}
											size="xs"
											c={node.status === "Skipped" || node.status === "Pending" ? "dimmed" : undefined}
											td={node.status === "Skipped" ? "line-through" : undefined}
											fw={node.key === active?.key ? 700 : undefined}
											data-testid={`chat-workflow-status-node-${node.key}`}
											data-status={node.status}
										>
											{`${workflowNodeGlyph(node.status)} ${node.label}`}
										</Text>
									))}
								</Group>
							</Fragment>
						))}
					</Group>
				) : null}
				{active ? (
					<Text size="xs" c="dimmed" data-testid="chat-workflow-status-active">
						{[
							active.label,
							...(active.model === undefined
								? []
								: [activeModel ?? active.model ?? t("pages.chat.workflow.status.defaultModel", "default model")]),
							...(active.startedAtUtc == null ? [] : [formatWorkflowElapsed(now - active.startedAtUtc)]),
						].join(" · ")}
					</Text>
				) : null}
				{active ? liveDetail : null}
				{terminalText ? (
					<Text size="xs" c={narrowed === "Failed" ? "red" : "dimmed"} data-testid="chat-workflow-status-terminal">
						{terminalText}
					</Text>
				) : null}
			</Stack>
			{onSteer ? <SteerDialog target={steerTarget} onClose={() => setSteerTarget(undefined)} onSteer={onSteer} /> : null}
		</Paper>
	);
}
