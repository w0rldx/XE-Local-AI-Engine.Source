import { Collapse, Group, Paper, Stack, Text, ThemeIcon, Tooltip, useComputedColorScheme } from "@mantine/core";
import { IconBrain, IconChevronDown, IconInfoCircle } from "@tabler/icons-react";
import { m, useReducedMotion } from "framer-motion";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { CHAT_ACCENT, CHAT_ACCENT_SOFT } from "@/features/chat/components/ChatVisualTokens";
import { StreamCaret } from "@/features/chat/components/StreamCaret";
import classes from "@/features/chat/components/ThoughtsSection.module.css";

/* eslint-disable react-doctor/no-adjust-state-on-prop-change */

interface ThoughtsSectionProps {
	messageId: string;
	reasoning?: string;
	streamingContent?: string;
	streamingOverflowBytes?: number;
	isStreaming?: boolean;
	hasContentStarted?: boolean;
	// True when the active reasoning effort is "none" yet the model emitted reasoning anyway. The reasoning is
	// still shown (decision: never hide it); this just surfaces an info note explaining the discrepancy.
	reasoningBypassed?: boolean;
}

function hasText(value?: string | null): value is string {
	return typeof value === "string" && value.trim().length > 0;
}

function words(value: string): number {
	return value.match(/\S+/g)?.length ?? 0;
}

function formatOverflow(bytes: number): string {
	return bytes < 1024 ? `+${bytes} B` : `+${Math.ceil(bytes / 1024)} KB`;
}

export function ThoughtsSection({
	messageId,
	reasoning,
	streamingContent = "",
	streamingOverflowBytes = 0,
	isStreaming = false,
	hasContentStarted = false,
	reasoningBypassed = false,
}: ThoughtsSectionProps) {
	const { t } = useTranslation();
	const colorScheme = useComputedColorScheme("light");
	const reduced = useReducedMotion();
	const streamingBodyRef = useRef<HTMLDivElement>(null);
	const startedAtRef = useRef<number | null>(null);
	const finalReasoning = hasText(reasoning) ? reasoning : undefined;
	const liveReasoning = hasText(streamingContent) ? streamingContent : undefined;
	const finalWordCount = useMemo(() => words(finalReasoning ?? ""), [finalReasoning]);
	const showsStreaming = isStreaming && liveReasoning !== undefined;
	const [elapsed, setElapsed] = useState(0);
	const [expanded, setExpanded] = useState(false);

	useEffect(() => {
		if (!isStreaming) {
			startedAtRef.current = null;
			return undefined;
		}

		if (startedAtRef.current === null) {
			startedAtRef.current = Date.now();
			setElapsed(0);
		}

		const id = window.setInterval(() => {
			if (startedAtRef.current !== null) {
				setElapsed(Math.floor((Date.now() - startedAtRef.current) / 1000));
			}
		}, 1000);

		return () => window.clearInterval(id);
	}, [isStreaming]);

	useEffect(() => {
		if (isStreaming && liveReasoning !== undefined && streamingBodyRef.current) {
			streamingBodyRef.current.scrollTop = streamingBodyRef.current.scrollHeight;
		}
	}, [isStreaming, liveReasoning]);

	if (showsStreaming) {
		return (
			<Paper className={classes["streaming"]} data-color-scheme={colorScheme} data-testid="chat-live-reasoning-stream" p="sm">
				<Stack gap="xs">
					<Group gap="xs" wrap="nowrap" align="center">
						<ThemeIcon size={22} radius="xl" variant="filled" style={{ background: CHAT_ACCENT_SOFT, color: CHAT_ACCENT }}>
							<m.div
								style={{ display: "inline-flex" }}
								animate={reduced ? { rotate: 0 } : { rotate: [0, 12, -8, 0] }}
								transition={reduced ? undefined : { duration: 2.4, repeat: Number.POSITIVE_INFINITY, ease: "easeInOut" }}
							>
								<IconBrain size={11} />
							</m.div>
						</ThemeIcon>
						<Text component="span" size="sm" fw={600}>
							{t("chat.toolCall.thinkingLive", "Thinking")}
						</Text>
						{hasContentStarted ? null : (
							<Text size="xs" c="dimmed" ff="monospace">
								· {elapsed}s
							</Text>
						)}
					</Group>
					<Text ref={streamingBodyRef} component="div" size="sm" className={classes["streaming-body"]}>
						{liveReasoning}
						<StreamCaret />
					</Text>
					{streamingOverflowBytes > 0 ? (
						<Text size="xs" c="dimmed">
							{formatOverflow(streamingOverflowBytes)}
						</Text>
					) : null}
				</Stack>
			</Paper>
		);
	}

	if (finalReasoning === undefined) {
		return null;
	}

	return (
		<div className={classes["section"]}>
			<div className={classes["details"]}>
				{/* Native <details> drives the summary toggle + a11y; the body lives in a Mantine Collapse (NOT inside
				    the <details>) so it animates BOTH open and close — a body nested in <details> is hidden instantly
				    by the browser on close, which is why the close used to snap. */}
				<details
					data-testid={`chat-message-reasoning-${messageId}`}
					open={expanded}
					onToggle={(event) => setExpanded(event.currentTarget.open)}
				>
					<summary
						className={`${classes["summary"]} mantine-focus-auto`}
						data-testid={`chat-message-reasoning-summary-${messageId}`}
					>
						<span className={classes["summary-content"]}>
							<Group gap="xs" wrap="nowrap" align="center">
								<ThemeIcon size={22} radius="xl" variant="filled" style={{ background: CHAT_ACCENT_SOFT, color: CHAT_ACCENT }}>
									<IconBrain size={11} />
								</ThemeIcon>
								<Text component="span" size="sm" fw={600} c="dimmed">
									{t("chat.thoughts", "Thoughts")} · {finalWordCount} {t("chat.words", "words")}
								</Text>
								{reasoningBypassed ? (
									<Tooltip
										multiline={true}
										maw={260}
										withArrow={true}
										label={t("chat.reasoningBypassNote", "This model may emit reasoning regardless of the 'none' setting.")}
									>
										<ThemeIcon
											size={18}
											radius="xl"
											variant="subtle"
											color="gray"
											data-testid={`chat-message-reasoning-bypass-${messageId}`}
										>
											<IconInfoCircle size={14} />
										</ThemeIcon>
									</Tooltip>
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
				<Collapse expanded={expanded} keepMounted={true} transitionDuration={reduced ? 0 : 240}>
					<Text component="div" size="sm" className={classes["reasoning-text"]}>
						{finalReasoning}
					</Text>
				</Collapse>
			</div>
		</div>
	);
}
