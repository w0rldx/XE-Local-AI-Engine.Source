import { Badge, Box, Button, Group, Stack, Text } from "@mantine/core";
import { IconCheck, IconWorldSearch, IconX } from "@tabler/icons-react";
import { useId } from "react";
import { useTranslation } from "react-i18next";

import classes from "@/features/chat/components/ThoughtsSection.module.css";
import type { NodeChatWebReviewPreviewDto } from "@/features/chat/models/NodeChatStreamTypes";

interface WebReviewCardProps {
	preview: NodeChatWebReviewPreviewDto;
	busy: boolean;
	onAccept: () => void;
	onReject: () => void;
}

// Everything in the preview came off the web and is untrusted: it renders as React text only (never markdown, never
// HTML), and URLs stay plain text rather than links, so nothing on the card can be clicked into or executed.
const plainTextStyle = { whiteSpace: "pre-wrap", overflowWrap: "anywhere", wordBreak: "break-word" } as const;
// A height-limited, keyboard-scrollable region (focusable so arrow keys scroll it without a pointer).
const scrollRegion = { mah: 320, style: { overflow: "auto" }, tabIndex: 0, role: "region" } as const;

/**
 * The web content review gate (ADR 0017, D8): what `web_fetch`/`web_search` retrieved, shown before the model reads it.
 * Accept approves the parked call (the content enters the conversation), Reject denies it (the model is told the user
 * declined). There is deliberately no approve-for-session: every retrieval is its own decision.
 */
export function WebReviewCard({ preview, busy, onAccept, onReject }: WebReviewCardProps) {
	const { t } = useTranslation();
	const headingId = useId();
	const isSearch = preview.toolName === "web_search";
	const finalUrl = preview.finalUrl && preview.finalUrl !== preview.url ? preview.finalUrl : undefined;

	return (
		<Stack
			gap="xs"
			className={classes["tool-body"]}
			role="group"
			aria-labelledby={headingId}
			data-testid={`chat-web-review-${preview.toolName}`}
		>
			<Group gap={6} wrap="nowrap">
				<IconWorldSearch size={14} aria-hidden={true} />
				<Text id={headingId} size="sm" fw={600}>
					{t("chat.webReview.heading", "Review web content before the model reads it")}
				</Text>
			</Group>
			{isSearch ? (
				<Stack gap={6}>
					{preview.backend ? (
						<Text size="xs" c="dimmed">
							{t("chat.webReview.backend", "Search engine: {{backend}}", { backend: preview.backend })}
						</Text>
					) : null}
					{(preview.results?.length ?? 0) === 0 ? (
						<Text size="xs" c="dimmed">
							{t("chat.webReview.noResults", "The search returned no results.")}
						</Text>
					) : null}
					<Box {...scrollRegion} aria-label={t("chat.webReview.resultsLabel", "Search results")}>
						<Stack gap={8} component="ol" m={0} pl="md" data-testid="chat-web-review-results">
							{preview.results?.map((result, index) => (
								// biome-ignore lint/suspicious/noArrayIndexKey: a fixed, server-ordered list that never reorders.
								<li key={index}>
									<Text size="sm" fw={500} style={plainTextStyle}>
										{result.title}
									</Text>
									<Text size="xs" c="dimmed" ff="monospace" style={plainTextStyle}>
										{result.url}
									</Text>
									<Text size="xs" style={plainTextStyle}>
										{result.snippet}
									</Text>
								</li>
							))}
						</Stack>
					</Box>
				</Stack>
			) : (
				<Stack gap={6}>
					{preview.title ? (
						<Text size="sm" fw={500} style={plainTextStyle}>
							{preview.title}
						</Text>
					) : null}
					{preview.url ? (
						<Text size="xs" c="dimmed" ff="monospace" style={plainTextStyle}>
							{preview.url}
						</Text>
					) : null}
					{finalUrl ? (
						<Text size="xs" c="dimmed" ff="monospace" style={plainTextStyle}>
							{t("chat.webReview.redirectedTo", "Redirected to {{url}}", { url: finalUrl })}
						</Text>
					) : null}
					<Group gap={6}>
						{preview.contentType ? (
							<Badge size="xs" variant="light" color="gray" radius="sm" tt="none">
								{preview.contentType}
							</Badge>
						) : null}
						{preview.truncated ? (
							<Badge size="xs" variant="light" color="orange" radius="sm">
								{t("chat.webReview.truncated", "Truncated")}
							</Badge>
						) : null}
					</Group>
					<Box {...scrollRegion} aria-label={t("chat.webReview.textLabel", "Retrieved page text")}>
						<Text component="pre" size="xs" m={0} ff="monospace" style={plainTextStyle} data-testid="chat-web-review-text">
							{preview.text ?? ""}
						</Text>
					</Box>
				</Stack>
			)}
			<Group gap="xs" wrap="wrap">
				<Button
					size="compact-xs"
					color="teal"
					variant="light"
					leftSection={<IconCheck size={12} />}
					loading={busy}
					onClick={onAccept}
					data-testid={`chat-web-review-accept-${preview.toolName}`}
				>
					{t("chat.webReview.accept", "Add to conversation")}
				</Button>
				<Button
					size="compact-xs"
					color="red"
					variant="light"
					leftSection={<IconX size={12} />}
					loading={busy}
					onClick={onReject}
					data-testid={`chat-web-review-reject-${preview.toolName}`}
				>
					{t("chat.webReview.reject", "Reject")}
				</Button>
			</Group>
		</Stack>
	);
}
