import { ActionIcon, Box, Button, FileButton, Group, Menu, Textarea, Tooltip } from "@mantine/core";
import {
	IconAdjustments,
	IconBrain,
	IconDeviceDesktop,
	IconPaperclip,
	IconPhoto,
	IconPlayerStopFilled,
	IconSend,
} from "@tabler/icons-react";
import { type DragEvent, useState } from "react";
import { useTranslation } from "react-i18next";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { AgentSelectorCard } from "@/features/chat/components/AgentSelectorCard";
import { ChatAttachmentChips } from "@/features/chat/components/ChatAttachmentChips";
import { ChatSamplingOptionsDialog } from "@/features/chat/components/ChatSamplingOptionsDialog";
import { ContextUsageBadge } from "@/features/chat/components/ContextUsageBadge";
import { ModelSelectorCard } from "@/features/chat/components/ModelSelectorCard";
import type { ChatAttachment, PendingAttachmentUpload } from "@/features/chat/models/ChatAttachmentModels";
import { defaultChatUiCapabilities } from "@/features/chat/models/ChatCapabilityGates";
import type {
	AgentOption,
	ChatUiCapabilities,
	ContextUsageModel,
	ModelOption,
	ReasoningEffort,
} from "@/features/chat/models/ChatModels";

// The composer toolbar lives inside the Textarea's native bottomSection (Mantine 9.3+). The section has a fixed
// height driven by --input-bottom-section-height (28px default); we override it to fit the 36px control row and
// match the input's bottom padding so the typed text never sits under the toolbar.
const TOOLBAR_HEIGHT = "3rem";
const composerStyles = {
	input: { paddingBottom: `calc(${TOOLBAR_HEIGHT} + var(--mantine-spacing-xs))` },
	bottomSection: { height: TOOLBAR_HEIGHT },
} as const;

// Stable empty default for the optional agentOptions prop. A fresh `[]` in the destructuring default allocates a
// new array reference every render, which would defeat referential-equality checks downstream.
const EMPTY_AGENT_OPTIONS: readonly AgentOption[] = [];
// Stable empty defaults for the optional attachment props (same referential-stability reasoning as above).
const EMPTY_ATTACHMENTS: readonly ChatAttachment[] = [];
const EMPTY_PENDING_UPLOADS: readonly PendingAttachmentUpload[] = [];

// File-picker hint for the OS dialog. The server's extractor allowlist is the source of truth (it rejects
// anything unsupported); this only nudges the picker toward the common document/text/code formats we extract.
const ATTACHMENT_ACCEPT = ".txt,.md,.markdown,.csv,.json,.log,.pdf,.docx,text/*,application/pdf";

interface ChatInputAreaProps {
	availableReasoningEfforts: ReasoningEffort[];
	capabilities?: ChatUiCapabilities;
	contextUsage?: ContextUsageModel;
	disabled?: boolean;
	isSending: boolean;
	modelOptions: ModelOption[];
	// Cloud (Codex) model options forwarded to ModelSelectorCard. Optional; absent hides the cloud section.
	cloudModelOptions?: ModelOption[];
	modelSelectorDisabled?: boolean;
	sendDisabled?: boolean;
	selectedModel: string;
	reasoningEffort: ReasoningEffort;
	// Whether the active model advertises the Ollama `tools` capability. Combined with the node-wide
	// capability to gate the local-tool controls so a non-tool model never offers them.
	activeModelToolCapable?: boolean;
	toolsEnabled?: boolean;
	agentControlsAvailable?: boolean;
	agentModeEnabled?: boolean;
	selectedAgentId?: string;
	agentOptions?: readonly AgentOption[];
	// Conversation file attachments. The chip row + paperclip picker only render when the capability gate
	// (showFileAttachmentControls) is on; the handlers are wired from Chat.tsx via useConversationAttachments.
	attachments?: readonly ChatAttachment[];
	pendingUploads?: readonly PendingAttachmentUpload[];
	onUploadFiles?: (files: File[]) => void;
	onRemoveAttachment?: (fileId: string) => void;
	onCancel: () => void;
	onModelChange: (model: string) => void;
	onReasoningEffortChange: (effort: ReasoningEffort) => void;
	onToggleTools?: () => void;
	// Single merged agent control: "" => Default Assistant (agent mode off); any other id => enable mode + stamp it.
	onSelectAgent?: (agentId: string) => void;
	onSend: (content: string, effort: ReasoningEffort, model: string) => void;
}

function isEffortAvailable(effort: ReasoningEffort, availableEfforts: ReasoningEffort[]): boolean {
	return availableEfforts.includes(effort);
}

export function ChatInputArea({
	availableReasoningEfforts,
	capabilities = defaultChatUiCapabilities,
	contextUsage,
	disabled = false,
	isSending,
	modelOptions,
	cloudModelOptions,
	modelSelectorDisabled = false,
	sendDisabled: sendDisabledProp = false,
	selectedModel,
	reasoningEffort,
	activeModelToolCapable = false,
	toolsEnabled = false,
	agentControlsAvailable = false,
	agentModeEnabled = false,
	selectedAgentId = "",
	agentOptions = EMPTY_AGENT_OPTIONS,
	attachments = EMPTY_ATTACHMENTS,
	pendingUploads = EMPTY_PENDING_UPLOADS,
	onUploadFiles,
	onRemoveAttachment,
	onCancel,
	onModelChange,
	onReasoningEffortChange,
	onToggleTools,
	onSelectAgent,
	onSend,
}: ChatInputAreaProps) {
	const { t } = useTranslation();
	const [content, setContent] = useState("");
	const [samplingDialogOpen, setSamplingDialogOpen] = useState(false);
	// Drag-over highlight for the file drop target (only meaningful when file attachments are enabled).
	const [isDragActive, setDragActive] = useState(false);
	// Read developer mode directly from the global store — avoids prop-drilling through ChatDisplayShell.
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const trimmed = content.trim();
	const reasoningEnabled = reasoningEffort !== "none";
	const reasoningMenuDisabled = disabled || isSending || availableReasoningEfforts.length <= 1;
	// Local-tool controls require BOTH the node-wide capability AND the active model advertising the Ollama
	// `tools` capability — a model that can't call tools must never be offered them.
	const showLocalToolControls = capabilities.showLocalToolControls && activeModelToolCapable;
	const sendDisabled = isSending ? false : disabled || sendDisabledProp || !trimmed;
	// Agent selector is disabled while sending or when there are no agents to pick from.
	const agentSelectorDisabled = disabled || isSending || agentOptions.length === 0;
	// File attachments are offered only behind the capability gate, with a wired upload handler, and while the
	// composer is interactive (not disabled / mid-send).
	const fileAttachmentsEnabled = capabilities.showFileAttachmentControls && Boolean(onUploadFiles);
	const attachmentControlsDisabled = disabled || isSending;

	const handlePickFiles = (files: File[] | null): void => {
		if (files && files.length > 0) {
			onUploadFiles?.(files);
		}
	};

	const handleDragOver = (event: DragEvent<HTMLDivElement>): void => {
		if (!fileAttachmentsEnabled || attachmentControlsDisabled) {
			return;
		}
		event.preventDefault();
		setDragActive(true);
	};

	const handleDragLeave = (event: DragEvent<HTMLDivElement>): void => {
		event.preventDefault();
		setDragActive(false);
	};

	const handleDrop = (event: DragEvent<HTMLDivElement>): void => {
		if (!fileAttachmentsEnabled || attachmentControlsDisabled) {
			return;
		}
		event.preventDefault();
		setDragActive(false);
		const files = Array.from(event.dataTransfer.files);
		if (files.length > 0) {
			onUploadFiles?.(files);
		}
	};

	const submit = (): void => {
		if (!trimmed) {
			return;
		}

		const safeEffort = isEffortAvailable(reasoningEffort, availableReasoningEfforts)
			? reasoningEffort
			: (availableReasoningEfforts[0] ?? "none");
		onSend(trimmed, safeEffort, selectedModel);
		setContent("");
	};

	// The toolbar is hosted in the Textarea's bottomSection (rendered inside the input border, pointer-events:all —
	// so the Stop button stays interactive even while the input element itself is disabled during a send).
	const toolbar = (
		<Group justify="space-between" align="center" wrap="nowrap" gap="xs" style={{ width: "100%" }}>
			<Group gap={4} wrap="nowrap" style={{ flex: 1, minWidth: 0 }}>
				<ModelSelectorCard
					modelOptions={modelOptions}
					cloudModelOptions={cloudModelOptions}
					selectedModel={selectedModel}
					disabled={modelSelectorDisabled || isSending || modelOptions.length === 0}
					onModelChange={onModelChange}
				/>
				<Menu position="top-start" offset={8} withinPortal={true} disabled={reasoningMenuDisabled}>
					<Menu.Target>
						<Tooltip label={t("pages.chat.reasoningEffortLabel", "Reasoning effort")}>
							<ActionIcon
								size={36}
								variant={reasoningEnabled ? "light" : "subtle"}
								color={reasoningEnabled ? "primary" : "gray"}
								disabled={reasoningMenuDisabled}
								aria-label={t("pages.chat.reasoningEffortLabel", "Reasoning effort")}
								data-testid="chat-reasoning-effort-menu-trigger"
							>
								<IconBrain size={15} />
							</ActionIcon>
						</Tooltip>
					</Menu.Target>
					<Menu.Dropdown>
						<Menu.Label>{t("pages.chat.reasoningEffortLabel", "Reasoning effort")}</Menu.Label>
						{availableReasoningEfforts.map((effort) => (
							<Menu.Item
								key={effort}
								data-testid={`chat-reasoning-effort-option-${effort}`}
								onClick={() => onReasoningEffortChange(effort)}
								color={effort === reasoningEffort && effort !== "none" ? "primary" : undefined}
							>
								{t(`pages.chat.reasoningEffortOptions.${effort}`, effort)}
							</Menu.Item>
						))}
					</Menu.Dropdown>
				</Menu>
				{showLocalToolControls ? (
					<Tooltip
						label={
							toolsEnabled
								? t("pages.chat.localToolsEnabled", "Local tools enabled")
								: t("pages.chat.localToolsDisabled", "Local tools disabled")
						}
					>
						<ActionIcon
							size={36}
							variant={toolsEnabled ? "light" : "subtle"}
							color={toolsEnabled ? "primary" : "gray"}
							disabled={disabled || isSending || !onToggleTools}
							onClick={onToggleTools}
							aria-label={t("pages.chat.localToolsLabel", "Local tools")}
							aria-pressed={toolsEnabled}
							data-testid="chat-local-tools-toggle"
						>
							<IconDeviceDesktop size={15} />
						</ActionIcon>
					</Tooltip>
				) : null}
				{agentControlsAvailable ? (
					<AgentSelectorCard
						agentOptions={agentOptions}
						agentModeEnabled={agentModeEnabled}
						selectedAgentId={selectedAgentId}
						disabled={agentSelectorDisabled}
						onSelectAgent={onSelectAgent ?? (() => undefined)}
					/>
				) : null}
				{fileAttachmentsEnabled ? (
					<FileButton onChange={handlePickFiles} multiple={true} accept={ATTACHMENT_ACCEPT}>
						{(fileButtonProps) => (
							<Tooltip label={t("pages.chat.composer.attach", "Attach file")}>
								<ActionIcon
									{...fileButtonProps}
									size={36}
									variant="subtle"
									color="gray"
									disabled={attachmentControlsDisabled}
									aria-label={t("pages.chat.composer.attach", "Attach file")}
									data-testid="chat-attach-file-trigger"
								>
									<IconPaperclip size={15} />
								</ActionIcon>
							</Tooltip>
						)}
					</FileButton>
				) : null}
				{capabilities.showImageAttachmentControls ? (
					<Tooltip label={t("pages.chat.composer.image", "Attach image")}>
						<ActionIcon size={36} variant="subtle" color="gray" disabled={true} aria-label="Attach image">
							<IconPhoto size={15} />
						</ActionIcon>
					</Tooltip>
				) : null}
				{developerMode ? (
					<Tooltip label={t("pages.chat.composer.samplingOptions", "Advanced sampling options")}>
						<ActionIcon
							size={36}
							variant="subtle"
							color="gray"
							onClick={() => setSamplingDialogOpen(true)}
							aria-label={t("pages.chat.composer.samplingOptions", "Advanced sampling options")}
							data-testid="chat-sampling-options-trigger"
						>
							<IconAdjustments size={15} />
						</ActionIcon>
					</Tooltip>
				) : null}
				{contextUsage ? <ContextUsageBadge {...contextUsage} /> : null}
			</Group>
			<Button
				data-testid="chat-send-button"
				onClick={() => {
					if (isSending) {
						onCancel();
						return;
					}
					submit();
				}}
				disabled={sendDisabled}
				color={isSending ? "red" : "dark"}
				size="sm"
				style={{ flexShrink: 0 }}
				leftSection={isSending ? <IconPlayerStopFilled size={13} /> : <IconSend size={13} />}
				aria-label={isSending ? t("pages.chat.stop", "Stop") : t("pages.chat.send", "Send")}
			>
				{isSending ? t("pages.chat.stop", "Stop") : t("pages.chat.send", "Send")}
			</Button>
		</Group>
	);

	return (
		<Box
			data-testid="chat-input-area"
			onDragOver={fileAttachmentsEnabled ? handleDragOver : undefined}
			onDragLeave={fileAttachmentsEnabled ? handleDragLeave : undefined}
			onDrop={fileAttachmentsEnabled ? handleDrop : undefined}
			style={
				isDragActive
					? { outline: "2px dashed var(--mantine-color-primary-5)", outlineOffset: 4, borderRadius: "var(--mantine-radius-md)" }
					: undefined
			}
		>
			{developerMode ? (
				<ChatSamplingOptionsDialog
					opened={samplingDialogOpen}
					onClose={() => setSamplingDialogOpen(false)}
					maxContextTokens={contextUsage?.maxTokens}
				/>
			) : null}
			{fileAttachmentsEnabled ? (
				<ChatAttachmentChips
					attachments={[...attachments]}
					pendingUploads={[...pendingUploads]}
					onRemove={onRemoveAttachment ?? (() => undefined)}
					disabled={attachmentControlsDisabled}
				/>
			) : null}
			<Textarea
				data-testid="chat-input"
				placeholder={t("pages.chat.inputPlaceholder", "Message the local node")}
				value={content}
				onChange={(event) => setContent(event.currentTarget.value)}
				onKeyDown={(event) => {
					if (event.key === "Enter" && !event.shiftKey) {
						event.preventDefault();
						submit();
					}
				}}
				autosize={true}
				minRows={2}
				maxRows={8}
				radius="md"
				disabled={disabled || isSending}
				bottomSection={toolbar}
				styles={composerStyles}
			/>
		</Box>
	);
}
