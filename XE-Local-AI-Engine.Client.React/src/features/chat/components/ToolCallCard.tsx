import { Badge, Group, Stack, Text, ThemeIcon } from "@mantine/core";
import { IconArrowRight, IconCheck, IconLoader2, IconShieldHalf, IconTool, IconX } from "@tabler/icons-react";
import { m, useReducedMotion } from "framer-motion";
import { useTranslation } from "react-i18next";

import { CHAT_ACCENT, CHAT_ACCENT_SOFT } from "@/features/chat/components/ChatVisualTokens";
import classes from "@/features/chat/components/ThoughtsSection.module.css";
import type { ChatToolPart, ToolCallState } from "@/features/chat/models/ChatModels";

interface ToolCallCardProps {
	part: ChatToolPart;
}

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

/** Right-aligned at-a-glance status marker, occupying the slot where ThoughtsSection shows its chevron. */
function StatusIcon({ state }: { state: ToolCallState }) {
	const reduced = useReducedMotion();

	if (state === "requesting") {
		return (
			<m.span
				style={{ display: "inline-flex" }}
				animate={reduced ? { x: 0, opacity: 1 } : { x: [0, 4, 10], opacity: [0.5, 1, 0] }}
				transition={reduced ? { duration: 0 } : { duration: 1.1, repeat: Number.POSITIVE_INFINITY, ease: "easeOut" }}
			>
				<IconArrowRight size={14} />
			</m.span>
		);
	}

	if (state === "waiting") {
		return (
			<m.span
				style={{ display: "inline-flex" }}
				animate={reduced ? { rotate: 0 } : { rotate: 360 }}
				transition={reduced ? { duration: 0 } : { duration: 0.9, repeat: Number.POSITIVE_INFINITY, ease: "linear" }}
			>
				<IconLoader2 size={14} />
			</m.span>
		);
	}

	return (
		<ThemeIcon size={18} radius="xl" color={state === "received" ? "teal" : "red"} variant="light">
			{state === "received" ? <IconCheck size={12} /> : <IconX size={12} />}
		</ThemeIcon>
	);
}

/**
 * One state-driven tool card rendered in BOTH streaming and final states (no component swap on completion). It
 * wears the SAME disclosure chrome as {@link ThoughtsSection} (shared CSS module + accent ThemeIcon header) so a
 * tool block reads as a sibling of a reasoning block, but its body is always visible — `requesting`/`waiting`
 * show a live marker + args; `received` adds the result the instant the completed event lands; `failed` surfaces
 * the error. Args/result JSON is pretty-printed when parseable.
 */
export function ToolCallCard({ part }: ToolCallCardProps) {
	const { t } = useTranslation();
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
	const hasBody = Boolean(formattedArgs) || Boolean(formattedResult) || part.state === "received";

	return (
		<div className={classes["section"]}>
			<div className={classes["details"]} data-testid={`chat-tool-call-card-${part.name}`} data-state={part.state}>
				<div className={classes["tool-header"]}>
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
								{part.name || t("chat.toolCall.name", "tool")}
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
							{isLiveState(part.state) ? (
								<Text size="xs" c="dimmed">
									{t("chat.toolCall.live", "live")}
								</Text>
							) : null}
						</Group>
						<StatusIcon state={part.state} />
					</span>
				</div>
				{hasBody ? (
					<Stack gap={6} className={classes["tool-body"]}>
						{formattedArgs ? (
							<Stack gap={2}>
								<Text size="xs" c="dimmed" fw={600}>
									{t("chat.toolCall.argsLabel", "Arguments")}
								</Text>
								<Text component="pre" ff="monospace" fz="xs" style={{ margin: 0, overflowX: "auto", whiteSpace: "pre-wrap" }}>
									{formattedArgs}
								</Text>
							</Stack>
						) : null}
						{formattedResult ? (
							<Stack gap={2}>
								<Text size="xs" c="dimmed" fw={600}>
									{part.state === "failed" ? t("chat.toolCall.errorLabel", "Error") : t("chat.toolCall.resultLabel", "Result")}
								</Text>
								<Text
									component="pre"
									ff="monospace"
									fz="xs"
									c={part.state === "failed" ? "red" : undefined}
									style={{ margin: 0, overflowX: "auto", whiteSpace: "pre-wrap" }}
									data-testid={`chat-tool-call-result-${part.name}`}
								>
									{formattedResult}
								</Text>
							</Stack>
						) : part.state === "received" ? (
							<Text size="xs" c="dimmed" ff="monospace" data-testid={`chat-tool-call-no-output-${part.name}`}>
								{t("chat.toolCall.noOutput", "(no output)")}
							</Text>
						) : null}
					</Stack>
				) : null}
			</div>
		</div>
	);
}
