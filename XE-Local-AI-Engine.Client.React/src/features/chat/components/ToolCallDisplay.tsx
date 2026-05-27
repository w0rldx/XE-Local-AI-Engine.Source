import { Badge, Group, Paper, Stack, Text, ThemeIcon } from "@mantine/core";
import { IconArrowRight, IconCheck, IconLoader2, IconX } from "@tabler/icons-react";
import { m, useReducedMotion } from "framer-motion";

import type { ChatTimelineEntry, ChatToolCall, ToolCallState } from "@/features/chat/models/ChatModels";

interface ToolCallDisplayProps {
	calls: ChatToolCall[];
}

interface ChatActivityTimelineProps {
	entries: ChatTimelineEntry[];
}

function isLiveState(state: ToolCallState): boolean {
	return state === "requesting" || state === "waiting";
}

function color(state: ToolCallState): string {
	switch (state) {
		case "failed":
			return "red";
		case "waiting":
			return "orange";
		case "received":
			return "teal";
		case "requesting":
			return "blue";
		default:
			return "gray";
	}
}

function StatusIcon({ state }: { state: ToolCallState }) {
	const reduced = useReducedMotion();

	if (state === "requesting") {
		return (
			<m.span
				style={{ display: "inline-flex" }}
				animate={reduced ? { x: 0, opacity: 1 } : { x: [0, 4, 10], opacity: [0.5, 1, 0] }}
				transition={reduced ? { duration: 0 } : { duration: 1.1, repeat: Number.POSITIVE_INFINITY, ease: "easeOut" }}
			>
				<IconArrowRight size={12} />
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
				<IconLoader2 size={12} />
			</m.span>
		);
	}

	return (
		<ThemeIcon size={16} radius="xl" color={state === "received" ? "teal" : "red"} variant="light">
			{state === "received" ? <IconCheck size={12} /> : <IconX size={12} />}
		</ThemeIcon>
	);
}

export function ToolCallDisplay({ calls }: ToolCallDisplayProps) {
	if (calls.length === 0) {
		return null;
	}

	return (
		<Paper withBorder={true} p="xs" data-testid="chat-tool-call-group">
			<Stack gap={6}>
				{calls.map((call) => (
					<Group key={call.id} gap="xs" wrap="nowrap" align="flex-start" data-testid={`chat-tool-call-row-${call.name}`}>
						<StatusIcon state={call.state} />
						<Text component="span" ff="monospace" size="xs" fw={600} style={{ flex: 1 }}>
							{call.name}
						</Text>
						<Badge size="xs" variant="light" color={color(call.state)} radius="sm">
							{call.state}
						</Badge>
						{call.duration ? (
							<Text size="xs" c="dimmed" ff="monospace">
								{call.duration}
							</Text>
						) : null}
						{isLiveState(call.state) ? (
							<Text size="xs" c="dimmed">
								live
							</Text>
						) : null}
					</Group>
				))}
			</Stack>
		</Paper>
	);
}

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

export function ChatActivityTimeline({ entries }: ChatActivityTimelineProps) {
	if (entries.length === 0) {
		return null;
	}

	return (
		<Paper withBorder={true} p="sm" data-testid="chat-activity-timeline">
			<Stack gap="xs">
				<Group justify="space-between">
					<Text fw={600} size="sm">
						Activity
					</Text>
					<Badge color="gray" variant="light">
						{entries.length}
					</Badge>
				</Group>
				{entries.map((entry) => (
					<Paper key={entry.id} p="xs" withBorder={true} data-testid={`chat-activity-entry-${entry.toolName ?? entry.id}`}>
						<Stack gap={4}>
							<Badge color={entry.type === "Error" || entry.type === "WorkflowStepFailed" ? "red" : "teal"} variant="light" w="fit-content">
								{entry.type}
							</Badge>
							{entry.toolName ?? entry.title ? (
								<Text fw={500} size="sm">
									{entry.toolName ?? entry.title}
								</Text>
							) : null}
							{entry.content ? (
								<Text size="sm" style={{ whiteSpace: "pre-wrap" }}>
									{entry.content}
								</Text>
							) : null}
							{entry.toolArgs || entry.toolResult ? (
								<Text component="pre" ff="monospace" fz="xs" style={{ margin: 0, overflowX: "auto", whiteSpace: "pre-wrap" }}>
									{formatStructured(entry.toolArgs) ?? formatStructured(entry.toolResult)}
								</Text>
							) : null}
						</Stack>
					</Paper>
				))}
			</Stack>
		</Paper>
	);
}
