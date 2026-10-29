// The Assistant tab: a conversation about the workflow open on the canvas. Presentational; the conversation and the
// turn in flight live in `useGraphWorkflowDesigner`, and a proposal is reviewed on the canvas, not here.
//
// Replies render through the shared markdown view with images as text: a reply is model output, and an image would be
// fetched the moment it rendered.

import { Alert, Badge, Button, Group, Loader, Paper, Select, Stack, Text, Textarea } from "@mantine/core";
import { IconInfoCircle, IconPlus, IconSend } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { MarkdownView } from "@/core/ui/components/MarkdownView/MarkdownView";
import { withCloudModelLabels } from "@/features/graphWorkflows/components/config/GraphWorkflowSelectOptions";
import type { GraphWorkflowDesignerState } from "@/features/graphWorkflows/hooks/useGraphWorkflowDesigner";
import type { GraphWorkflowEditorMode } from "@/features/graphWorkflows/hooks/useGraphWorkflowEditor";

export interface GraphWorkflowAssistantPanelProps {
	readonly designer: GraphWorkflowDesignerState;
	readonly editorMode: GraphWorkflowEditorMode;
	/** The node the next turn is asked about, when one is selected. */
	readonly selectedNodeKey: string | undefined;
}

const starterKeys = ["explain", "risks", "addReview"] as const;

export function GraphWorkflowAssistantPanel({ designer, editorMode, selectedNodeKey }: GraphWorkflowAssistantPanelProps) {
	const { t } = useTranslation();
	const isGenerating = editorMode === "generating";
	const isReviewing = editorMode === "reviewing";
	const canSend = editorMode === "editing" && designer.modelName.length > 0 && designer.draft.trim().length > 0;
	const failure = designer.failure;

	const starters: Record<(typeof starterKeys)[number], string> = {
		explain: t("pages.graphWorkflows.designer.starter.explain", "Explain what this workflow does, step by step."),
		risks: t("pages.graphWorkflows.designer.starter.risks", "What could go wrong when this workflow runs?"),
		addReview: t("pages.graphWorkflows.designer.starter.addReview", "Add a human approval step before the workflow finishes."),
	};

	return (
		<Stack gap="sm" data-testid="gw-assistant">
			<Group gap="xs" wrap="nowrap" align="flex-end">
				<Select
					style={{ flex: 1, minWidth: 0 }}
					size="xs"
					label={t("pages.graphWorkflows.designer.model", "Model")}
					data={withCloudModelLabels(designer.modelOptions, t)}
					value={designer.modelName.length > 0 ? designer.modelName : null}
					onChange={designer.setModelName}
					disabled={isGenerating}
					allowDeselect={false}
					searchable={true}
					nothingFoundMessage={t("pages.graphWorkflows.designer.noModels", "No chat model is installed on this node.")}
					data-testid="gw-assistant-model"
				/>
				<Button
					size="xs"
					variant="default"
					leftSection={<IconPlus size={14} />}
					onClick={designer.newChat}
					disabled={isGenerating || designer.messages.length === 0}
					data-testid="gw-assistant-new-chat"
				>
					{t("pages.graphWorkflows.designer.newChat", "New chat")}
				</Button>
			</Group>

			{designer.messages.length === 0 ? (
				<Stack gap="xs" data-testid="gw-assistant-starters">
					<Text size="sm" c="dimmed">
						{t(
							"pages.graphWorkflows.designer.intro",
							"Ask about this workflow, or describe a change. A change is shown on the canvas for you to accept or reject; nothing is saved or run.",
						)}
					</Text>
					{starterKeys.map((key) => (
						<Button
							key={key}
							size="xs"
							variant="light"
							justify="flex-start"
							styles={{ label: { whiteSpace: "normal", textAlign: "left" } }}
							h="auto"
							py={6}
							disabled={isGenerating}
							onClick={() => designer.setDraft(starters[key])}
							data-testid={`gw-assistant-starter-${key}`}
						>
							{starters[key]}
						</Button>
					))}
				</Stack>
			) : (
				<Stack gap="xs" data-testid="gw-assistant-messages">
					{designer.messages.map((message, index) =>
						message.role === "user" ? (
							<Paper
								// The list only ever grows or empties, so a position is a stable identity.
								// biome-ignore lint/suspicious/noArrayIndexKey: append-only list, see above
								key={index}
								p="xs"
								bg="var(--mantine-color-default-hover)"
								data-testid="gw-assistant-message-user"
							>
								<Text size="sm" style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>
									{message.text}
								</Text>
							</Paper>
						) : (
							// biome-ignore lint/suspicious/noArrayIndexKey: append-only list, see above
							<div key={index} data-testid="gw-assistant-message-assistant">
								<MarkdownView content={message.text} images="link" />
							</div>
						),
					)}
				</Stack>
			)}

			{isGenerating ? (
				<Group gap="xs" justify="space-between" data-testid="gw-assistant-pending">
					<Group gap="xs">
						<Loader size="xs" />
						<Text size="sm" c="dimmed">
							{t("pages.graphWorkflows.designer.pending", "Working on it. The canvas is locked until the answer arrives.")}
						</Text>
					</Group>
					<Button size="xs" variant="light" color="red" onClick={designer.cancel} data-testid="gw-assistant-cancel">
						{t("pages.graphWorkflows.designer.cancel", "Cancel")}
					</Button>
				</Group>
			) : null}

			{isReviewing ? (
				<Text size="sm" c="dimmed" data-testid="gw-assistant-reviewing">
					{t(
						"pages.graphWorkflows.designer.reviewing",
						"The proposed change is on the canvas. Accept or reject it there before you send another message.",
					)}
				</Text>
			) : null}

			{failure?.kind === "busy" ? (
				<Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />} data-testid="gw-assistant-busy">
					{t("pages.graphWorkflows.designer.busy", "The node is running another task right now. Try again once it finishes.")}
				</Alert>
			) : null}
			{failure?.kind === "cloudRefused" ? (
				<Alert color="yellow" variant="light" icon={<IconInfoCircle size={16} />} data-testid="gw-assistant-cloud-refused">
					{t(
						"pages.graphWorkflows.designer.cloudRefused",
						"This cloud model may not read the workflow. Turn on “Let this connection's models read local data” for its connection, or “Let cloud models read local data” in the node settings, or pick a model on this node.",
					)}
					{/* The server's own sentence names the grant it checked. */}
					{failure.error instanceof Error && failure.error.message.length > 0 ? (
						<Text size="xs" c="dimmed" mt={4} data-testid="gw-assistant-cloud-refused-detail">
							{failure.error.message}
						</Text>
					) : null}
				</Alert>
			) : null}
			{failure?.kind === "unparseable" ? (
				<Alert color="yellow" variant="light" data-testid="gw-assistant-unparseable">
					{t(
						"pages.graphWorkflows.designer.unparseable",
						"The model did not return a usable answer. Try again, or pick a different model.",
					)}
				</Alert>
			) : null}
			{failure?.kind === "providerFailed" ? (
				<Alert color="yellow" variant="light" data-testid="gw-assistant-provider-failed">
					{t(
						"pages.graphWorkflows.designer.providerFailed",
						"The model provider rejected the request. Try again, or pick a different model.",
					)}
				</Alert>
			) : null}
			{failure?.kind === "reloaded" ? (
				<Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />} data-testid="gw-assistant-reloaded">
					{t("pages.graphWorkflows.designer.reloaded", "The workflow was reloaded, so the request was cancelled.")}
				</Alert>
			) : null}
			{failure?.kind === "error" ? (
				<InlineErrorAlert
					message={apiErrorMessage(failure.error, t("pages.graphWorkflows.designer.failed", "The assistant could not answer."))}
					data-testid="gw-assistant-error"
				/>
			) : null}

			{selectedNodeKey === undefined ? null : (
				<Group gap={4}>
					<Text size="xs" c="dimmed">
						{t("pages.graphWorkflows.designer.about", "About the selected node")}
					</Text>
					<Badge size="sm" variant="light" data-testid="gw-assistant-selected-node">
						{selectedNodeKey}
					</Badge>
				</Group>
			)}

			<Textarea
				aria-label={t("pages.graphWorkflows.designer.input", "Message")}
				placeholder={t("pages.graphWorkflows.designer.placeholder", "Ask a question or describe a change")}
				value={designer.draft}
				onChange={(event) => designer.setDraft(event.currentTarget.value)}
				onKeyDown={(event) => {
					// Enter sends, Shift+Enter breaks the line: the chat composer's convention.
					if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
						event.preventDefault();
						if (canSend) {
							designer.send();
						}
					}
				}}
				autosize={true}
				minRows={2}
				maxRows={8}
				disabled={isGenerating}
				data-testid="gw-assistant-input"
			/>
			<Group justify="flex-end">
				<Button
					size="xs"
					leftSection={<IconSend size={14} />}
					onClick={designer.send}
					disabled={!canSend}
					data-testid="gw-assistant-send"
				>
					{t("pages.graphWorkflows.designer.send", "Send")}
				</Button>
			</Group>
		</Stack>
	);
}
