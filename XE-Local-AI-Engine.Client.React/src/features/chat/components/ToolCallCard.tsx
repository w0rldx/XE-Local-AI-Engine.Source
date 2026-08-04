import { Badge, Button, Collapse, Group, Stack, Text, ThemeIcon } from "@mantine/core";
import { IconChevronDown, IconCheck, IconShieldHalf, IconTool, IconX } from "@tabler/icons-react";
import { useMutation } from "@tanstack/react-query";
import { m, useReducedMotion } from "framer-motion";
import { memo, useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { resolveToolApprovalMutation } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { CodeBlock } from "@/core/ui/components/CodeBlock/CodeBlock";
import { AskUserQuestionCard } from "@/features/chat/components/AskUserQuestionCard";
import { CHAT_ACCENT, CHAT_ACCENT_SOFT } from "@/features/chat/components/ChatVisualTokens";
import classes from "@/features/chat/components/ThoughtsSection.module.css";
import { buildChatUiCapabilities } from "@/features/chat/models/ChatCapabilityGates";
import type { ChatToolPart, ToolCallState } from "@/features/chat/models/ChatModels";
import { ToolCategoryBadge } from "@/features/tools/components/ToolCategoryBadge";
import { useToolCatalog } from "@/features/tools/queries/useToolCatalog";

interface ToolCallCardProps {
	part: ChatToolPart;
}

// Chat capability flags are a static client-side constant (see docs/agent-knowledge.md §5), so the tool-approval-controls gate
// is resolvable once at module scope rather than threaded down through the render tree.
const approvalControlsEnabled = buildChatUiCapabilities(nodeCapabilities.chat).showToolApprovalControls;

/**
 * Per-tool-call expand state, persisted across remounts and keyed by the stable tool-call id. When the final answer
 * arrives the assistant turn moves from the transient streaming placeholder into the persisted message list — a real
 * unmount/remount — so component-local `useState` would snap an operator-expanded card back to collapsed. This module
 * map carries the operator's choice through that swap. Default stays collapsed (no entry → false).
 */
const expandedByToolId = new Map<string, boolean>();

function isLiveState(state: ToolCallState): boolean {
	return state === "requesting" || state === "waiting";
}

function stateColor(state: ToolCallState): string {
	switch (state) {
		case "failed":
			return "red";
		case "waiting":
			return "orange";
		case "received":
			return "teal";
		default:
			return "blue";
	}
}

/** Pretty-prints a JSON args/result blob; non-JSON values pass through unchanged. */
function formatStructured(value?: string): string | undefined {
	if (!value) {
		return undefined;
	}

	try {
		return JSON.stringify(JSON.parse(value), null, 2);
	} catch {
		return value;
	}
}

/**
 * One state-driven tool card rendered in BOTH streaming and final states (no component swap on completion). It is a
 * collapsible disclosure that mirrors {@link ThoughtsSection} (shared CSS module + accent ThemeIcon header + chevron)
 * so a tool block reads as a sibling of a reasoning block. **Default minimized**: the header always shows the tool
 * name + at-a-glance state badge; expanding reveals the args/result body. `requesting`/`waiting` show a live badge,
 * `received` carries the result, `failed` surfaces the error. Args/result JSON is pretty-printed when parseable.
 */
export const ToolCallCard = memo(function ToolCallCard({ part }: ToolCallCardProps) {
	const { t } = useTranslation();
	const reduced = useReducedMotion();
	const [expanded, setExpanded] = useState(() => expandedByToolId.get(part.id) ?? false);

	// Resolve the tool's risk class + node-policy floor from the shared catalog (cached; reused across the app) so the
	// card can badge the tool's class next to the approval controls. A tool not found in the catalog (e.g. a since-removed
	// MCP server) fails closed to the "Unknown" class, which the badge treats as approval-requiring.
	const catalogQuery = useToolCatalog();
	const catalogEntry = catalogQuery.data?.find((tool) => tool.name === part.name);
	const toolCategory = catalogEntry?.category ?? "Unknown";
	const toolEffectiveApproval = catalogEntry?.effectiveRequiresApproval ?? true;

	const handleToggle = (open: boolean) => {
		expandedByToolId.set(part.id, open);
		setExpanded(open);
	};

	// Local tool-approval responder. The pending approval request id rides the part; posting the decision to
	// the loopback resolve endpoint releases the waiting run server-side. `decided` hides the controls the instant the
	// operator clicks (optimistic) — the card then clears naturally when the tool completes/rejects (tool-call-completed
	// clears pendingApprovalRequestId). No conversation context is needed: the request id is the global correlation key.
	const [decided, setDecided] = useState(false);
	const resolveApproval = useMutation({ ...withResponseValidation(resolveToolApprovalMutation()) });
	const pendingApprovalRequestId = part.pendingApprovalRequestId;
	const awaitingApproval =
		approvalControlsEnabled && part.state === "waiting" && !decided && typeof pendingApprovalRequestId === "string" && pendingApprovalRequestId.length > 0;

	const handleApprovalDecision = useCallback(
		(approved: boolean) => {
			if (!pendingApprovalRequestId) {
				return;
			}
			setDecided(true);
			resolveApproval.mutate(
				{ body: { requestId: pendingApprovalRequestId, approved } },
				{
					// Re-arm the controls if the post failed so the operator can retry rather than being stuck with a
					// dismissed prompt on a still-waiting tool.
					onError: () => setDecided(false),
				},
			);
		},
		[pendingApprovalRequestId, resolveApproval],
	);

	const formattedArgs = formatStructured(part.args);
	const formattedResult = formatStructured(part.result);
	const stateLabel =
		part.state === "requesting"
			? t("chat.toolCall.sending", "Sending")
			: part.state === "waiting"
				? t("chat.toolCall.inFlight", "In flight")
				: part.state === "received"
					? t("chat.toolCall.done", "Done")
					: t("chat.toolCall.failed", "Failed");
	const toolName = part.name || t("chat.toolCall.name", "tool");

	return (
		<div className={classes["section"]}>
			{/* Native <details> drives the summary toggle + a11y; the body lives in a Mantine Collapse (outside the
			    <details>) so it animates open AND close — consistent with ThoughtsSection. A body nested in <details> is
			    hidden instantly by the browser on close, which is why the close used to snap. */}
			<div className={classes["details"]} data-testid={`chat-tool-call-card-${part.name}`} data-state={part.state}>
				<details
					data-testid={`chat-tool-call-disclosure-${part.name}`}
					open={expanded}
					onToggle={(event) => handleToggle(event.currentTarget.open)}
				>
					<summary className={`${classes["summary"]} mantine-focus-auto`} data-testid={`chat-tool-call-summary-${part.name}`}>
					<span className={classes["summary-content"]}>
						<Group gap="xs" wrap="nowrap" align="center" style={{ minWidth: 0 }}>
							<ThemeIcon size={22} radius="xl" variant="filled" style={{ background: CHAT_ACCENT_SOFT, color: CHAT_ACCENT }}>
								<IconTool size={11} />
							</ThemeIcon>
							<Text
								component="span"
								ff="monospace"
								size="sm"
								fw={600}
								c="dimmed"
								style={{ minWidth: 0, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}
							>
								{toolName}
							</Text>
							{part.requiresApproval ? (
								<ThemeIcon
									size={16}
									radius="xl"
									color="orange"
									variant="light"
									data-testid={`chat-tool-call-approval-${part.name}`}
								>
									<IconShieldHalf size={11} />
								</ThemeIcon>
							) : null}
							<Badge size="xs" variant="light" color={stateColor(part.state)} radius="sm">
								{stateLabel}
							</Badge>
							<ToolCategoryBadge category={toolCategory} effectiveRequiresApproval={toolEffectiveApproval} />
							{isLiveState(part.state) ? (
								<Text size="xs" c="dimmed">
									{t("chat.toolCall.live", "live")}
								</Text>
							) : null}
						</Group>
						<m.span
							style={{ display: "inline-flex" }}
							animate={{ rotate: expanded ? 0 : -90 }}
							transition={reduced ? { duration: 0 } : { duration: 0.2 }}
						>
							<IconChevronDown size={14} />
						</m.span>
					</span>
				</summary>
				</details>
				{part.pendingQuestion ? (
					<AskUserQuestionCard pending={part.pendingQuestion} />
				) : awaitingApproval ? (
					<Group gap="xs" wrap="nowrap" className={classes["tool-body"]} data-testid={`chat-tool-call-approval-actions-${part.name}`}>
						<Text size="xs" c="dimmed" style={{ minWidth: 0, overflow: "hidden", textOverflow: "ellipsis" }}>
							{t("chat.toolCall.approvalPrompt", "This tool needs your approval to run.")}
						</Text>
						<Button
							size="compact-xs"
							color="teal"
							variant="light"
							leftSection={<IconCheck size={12} />}
							loading={resolveApproval.isPending}
							onClick={() => handleApprovalDecision(true)}
							data-testid={`chat-tool-call-approve-${part.name}`}
						>
							{t("chat.toolCall.approve", "Approve")}
						</Button>
						<Button
							size="compact-xs"
							color="red"
							variant="light"
							leftSection={<IconX size={12} />}
							loading={resolveApproval.isPending}
							onClick={() => handleApprovalDecision(false)}
							data-testid={`chat-tool-call-deny-${part.name}`}
						>
							{t("chat.toolCall.deny", "Deny")}
						</Button>
					</Group>
				) : null}
				<Collapse expanded={expanded} keepMounted={true} transitionDuration={reduced ? 0 : 240}>
				<Stack gap={6} className={classes["tool-body"]}>
					{formattedArgs ? (
						<Stack gap={2}>
							<Text size="xs" c="dimmed" fw={600}>
								{t("chat.toolCall.argsLabel", "Arguments")}
							</Text>
							<CodeBlock language="json" code={formattedArgs} />
						</Stack>
					) : null}
					{formattedResult ? (
						<Stack gap={2}>
							<Text size="xs" c={part.state === "failed" ? "red" : "dimmed"} fw={600}>
								{part.state === "failed" ? t("chat.toolCall.errorLabel", "Error") : t("chat.toolCall.resultLabel", "Result")}
							</Text>
							<div data-testid={`chat-tool-call-result-${part.name}`}>
								<CodeBlock language="json" code={formattedResult} />
							</div>
						</Stack>
					) : part.state === "received" ? (
						<Text size="xs" c="dimmed" ff="monospace" data-testid={`chat-tool-call-no-output-${part.name}`}>
							{t("chat.toolCall.noOutput", "(no output)")}
						</Text>
					) : null}
				</Stack>
				</Collapse>
			</div>
		</div>
	);
});
