import { ActionIcon, Box, Button, FileButton, Group, Menu, Text, Textarea, Tooltip } from "@mantine/core";
import {
	IconAdjustments,
	IconBooks,
	IconBrain,
	IconDeviceDesktop,
	IconPaperclip,
	IconPlayerStopFilled,
	IconSend,
} from "@tabler/icons-react";
import { type DragEvent, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { AgentSelectorCard } from "@/features/chat/components/AgentSelectorCard";
import { ChatAttachmentChips } from "@/features/chat/components/ChatAttachmentChips";
import { ChatSamplingOptionsDialog } from "@/features/chat/components/ChatSamplingOptionsDialog";
import { SlashCommandAutocomplete } from "@/features/chat/components/SlashCommandAutocomplete";
import { CompactButton } from "@/features/chat/components/CompactButton";
import { ContextUsageBadge } from "@/features/chat/components/ContextUsageBadge";
import { ModelSelectorCard } from "@/features/chat/components/ModelSelectorCard";
import type { ChatAttachment, PendingAttachmentUpload } from "@/features/chat/models/ChatAttachmentModels";
import { defaultChatUiCapabilities } from "@/features/chat/models/ChatCapabilityGates";
import { evaluateComposerSize, toDisplayKb } from "@/features/chat/models/ComposerSizeLimit";
import type { ChatCommandOption } from "@/features/chat/models/SlashCommandModels";
import { resolveSlashCommand } from "@/features/chat/models/SlashCommandResolver";
import { useSlashCommandAutocomplete } from "@/features/chat/hooks/useSlashCommandAutocomplete";
import type {
	AgentOption,
	ChatUiCapabilities,
	ContextUsageModel,
	ModelOption,
	ReasoningEffort,
} from "@/features/chat/models/ChatModels";
import { VoiceComposerControls } from "@/features/voice/components/VoiceComposerControls";

// The composer toolbar lives inside the Textarea's native bottomSection (Mantine 9.3+), which is absolutely
// positioned at a fixed height — it does not grow to fit its content. On wide viewports the toolbar is one
// 36px row and a static height works, but on narrow panes the controls wrap to two or three rows (see the
// toolbar's `wrap="wrap"` below), so the height is measured live via ResizeObserver (below) and both the
// section height and the input's reserved bottom padding are driven from that measurement — otherwise a
// wrapped toolbar either gets clipped or the typed text renders underneath it.
const DEFAULT_TOOLBAR_HEIGHT_PX = 48;
// Below this window width the context-usage badge is dropped from the toolbar entirely rather than adding yet
// another row — it's the least essential control and the composer needs the space more on very narrow phones.
const CONTEXT_USAGE_HIDE_WIDTH = 480;

// Stable empty default for the optional agentOptions prop. A fresh `[]` in the destructuring default allocates a
// new array reference every render, which would defeat referential-equality checks downstream.
const EMPTY_AGENT_OPTIONS: readonly AgentOption[] = [];
// Stable empty defaults for the optional attachment props (same referential-stability reasoning as above).
const EMPTY_ATTACHMENTS: readonly ChatAttachment[] = [];
const EMPTY_PENDING_UPLOADS: readonly PendingAttachmentUpload[] = [];
const EMPTY_COMMAND_OPTIONS: readonly ChatCommandOption[] = [];

// File-picker hint for the OS dialog. The server's extractor allowlist is the source of truth (it rejects
// anything unsupported); this only nudges the picker toward the common document/text/code formats we extract.
const ATTACHMENT_ACCEPT = ".txt,.md,.markdown,.csv,.json,.log,.pdf,.docx,text/*,application/pdf";
// Image accept hint, appended to ATTACHMENT_ACCEPT only when the active model is multimodal (vision-capable via a
// local mmproj projector) — mirrors ATTACHMENT_ACCEPT's role as an OS-dialog nudge, not the authoritative allowlist.
const IMAGE_ATTACHMENT_ACCEPT = ".png,.jpg,.jpeg,.webp,.gif,image/*";

interface ChatInputAreaProps {
	availableReasoningEfforts: ReasoningEffort[];
	capabilities?: ChatUiCapabilities;
	contextUsage?: ContextUsageModel;
	// The node's effective Security:MaxMessageSizeKb. Drives the pre-send size check below; absent (not loaded yet, or
	// a node that omits it) means no pre-check at all — the hub still enforces the cap.
	maxMessageSizeKb?: number;
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
	// Whether the active model has a local mmproj vision projector. Combined with the node-wide capability
	// (showImageAttachmentControls) to gate whether the composer accepts image attachments — mirrors
	// activeModelToolCapable's role for the local-tool controls.
	activeModelMultimodal?: boolean;
	// Opt-in knowledge-base grounding for plain chat. The toggle renders only when the node ships the
	// knowledge-base surface (capabilities.showKnowledgeBaseControls) and is hidden in agent mode (the agent uses the
	// search_knowledge_base tool instead).
	knowledgeBaseEnabled?: boolean;
	// Whether the node has at least one INDEXED knowledge document (Status Indexed or ChunkCount>0). The KB toggle stays
	// visible whenever the feature is on but is disabled (with a "no documents" tooltip) until there is something to
	// search — grounding on an empty corpus is a no-op. Defaults to true so callers that don't wire it keep it enabled.
	knowledgeBaseHasDocuments?: boolean;
	agentControlsAvailable?: boolean;
	agentModeEnabled?: boolean;
	selectedAgentId?: string;
	agentOptions?: readonly AgentOption[];
	commandOptions?: readonly ChatCommandOption[];
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
	onToggleKnowledgeBase?: () => void;
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
	maxMessageSizeKb,
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
	activeModelMultimodal = false,
	knowledgeBaseEnabled = false,
	knowledgeBaseHasDocuments = true,
	agentControlsAvailable = false,
	agentModeEnabled = false,
	selectedAgentId = "",
	agentOptions = EMPTY_AGENT_OPTIONS,
	commandOptions = EMPTY_COMMAND_OPTIONS,
	attachments = EMPTY_ATTACHMENTS,
	pendingUploads = EMPTY_PENDING_UPLOADS,
	onUploadFiles,
	onRemoveAttachment,
	onCancel,
	onModelChange,
	onReasoningEffortChange,
	onToggleTools,
	onToggleKnowledgeBase,
	onSelectAgent,
	onSend,
}: ChatInputAreaProps) {
	const { t } = useTranslation();
	const [content, setContent] = useState("");
	const [selection, setSelection] = useState({ start: 0, end: 0 });
	const [isComposing, setIsComposing] = useState(false);
	const inputRef = useRef<HTMLTextAreaElement>(null);
	const pendingFocusCaret = useRef<number | null>(null);
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
	// The knowledge-base grounding toggle shows for plain chat only: agent mode reaches the knowledge base through the
	// search_knowledge_base tool, so an extra inline-grounding toggle there would be redundant and confusing.
	const showKnowledgeBaseControls = capabilities.showKnowledgeBaseControls && !agentModeEnabled;
	// Pre-send mirror of the node's message-size cap. Measured against `trimmed` — the string the send path actually
	// puts on the wire — so the byte count matches what LocalChatHub.EnsureMessageWithinSizeCap will measure. (A draft
	// that resolves to a slash command sends the command's prompt instead, but such a draft is a few characters long,
	// so this can never raise a FALSE warning; the server still enforces that case.)
	const sizeState = useMemo(() => evaluateComposerSize(trimmed, maxMessageSizeKb), [trimmed, maxMessageSizeKb]);
	const overSizeLimit = sizeState?.overLimit === true;
	const sendDisabled = isSending ? false : disabled || sendDisabledProp || !trimmed || overSizeLimit;
	// Agent selector is disabled while sending or when there are no agents to pick from.
	const agentSelectorDisabled = disabled || isSending || agentOptions.length === 0;
	// File attachments are offered only behind the capability gate, with a wired upload handler, and while the
	// composer is interactive (not disabled / mid-send).
	const fileAttachmentsEnabled = capabilities.showFileAttachmentControls && Boolean(onUploadFiles);
	// Image attachments require BOTH the node-wide capability AND the active model advertising a local mmproj
	// vision projector — mirrors showLocalToolControls's per-model gating above.
	const imageAttachmentsEnabled = capabilities.showImageAttachmentControls && activeModelMultimodal && Boolean(onUploadFiles);
	// Either capability lights up the paperclip / chip row / drag-drop surface; the accept string and upload
	// handler still differ per attachment kind via imageAttachmentsEnabled below.
	const attachmentControlsAvailable = fileAttachmentsEnabled || imageAttachmentsEnabled;
	const attachmentAccept = imageAttachmentsEnabled ? `${ATTACHMENT_ACCEPT},${IMAGE_ATTACHMENT_ACCEPT}` : ATTACHMENT_ACCEPT;
	const attachmentControlsDisabled = disabled || isSending;
	// Voice controls are dev-gated AND require the operator-owned node gate (capabilities.showVoiceControls,
	// derived from manifest.Enabled). The leaf components additionally self-gate on the runtime context.
	const showVoiceControls = developerMode && capabilities.showVoiceControls;
	const { width } = useWindowDimensions();
	const showContextUsage = Boolean(contextUsage) && width >= CONTEXT_USAGE_HIDE_WIDTH;
	const autocomplete = useSlashCommandAutocomplete({
		content,
		selectionStart: selection.start,
		selectionEnd: selection.end,
		interactive: !disabled && !isSending,
		isComposing,
		options: commandOptions,
		onSelect: (option) => {
			const canonical = `/${option.name}`;
			setContent(canonical);
			setSelection({ start: canonical.length, end: canonical.length });
			pendingFocusCaret.current = canonical.length;
		},
	});

	useEffect(() => {
		const caret = pendingFocusCaret.current;
		if (caret === null || content.length !== caret) {
			return;
		}
		pendingFocusCaret.current = null;
		inputRef.current?.focus();
		inputRef.current?.setSelectionRange(caret, caret);
	}, [content]);

	// Measures the toolbar's actual rendered height (it wraps to 2-3 rows on narrow panes) so the Textarea's
	// fixed-height bottomSection and its own bottom padding can be kept in sync with however tall the toolbar
	// really is — a static height either clips a wrapped toolbar or lets typed text render underneath it.
	const toolbarRef = useRef<HTMLDivElement>(null);
	const [toolbarHeight, setToolbarHeight] = useState(DEFAULT_TOOLBAR_HEIGHT_PX);

	useEffect(() => {
		const node = toolbarRef.current;
		if (!node || typeof ResizeObserver === "undefined") {
			return undefined;
		}

		const observer = new ResizeObserver((entries) => {
			const measuredHeight = entries[0]?.contentRect.height;
			if (measuredHeight) {
				setToolbarHeight(measuredHeight);
			}
		});
		observer.observe(node);
		return () => observer.disconnect();
	}, []);

	// bottomSection is border-box: its fixed height must include its own paddingBlock (2 × xs) on top of the
	// measured toolbar height, otherwise the last wrapped toolbar row clips past the composer's bottom edge.
	const composerStyles = {
		input: { paddingBottom: `calc(${toolbarHeight}px + 3 * var(--mantine-spacing-xs))` },
		bottomSection: {
			height: `calc(${toolbarHeight}px + 2 * var(--mantine-spacing-xs))`,
			alignItems: "flex-start",
			paddingBlock: "var(--mantine-spacing-xs)",
		},
	};

	const handlePickFiles = (files: File[] | null): void => {
		if (files && files.length > 0) {
			onUploadFiles?.(files);
		}
	};

	const handleDragOver = (event: DragEvent<HTMLDivElement>): void => {
		if (!attachmentControlsAvailable || attachmentControlsDisabled) {
			return;
		}
		// Stop the event reaching the chat-pane-level drop zone (ChatDisplayShell) so a drop on the composer is handled
		// here once, not also by the pane — and the pane overlay stays hidden while hovering the composer.
		event.stopPropagation();
		event.preventDefault();
		setDragActive(true);
	};

	const handleDragLeave = (event: DragEvent<HTMLDivElement>): void => {
		event.stopPropagation();
		event.preventDefault();
		setDragActive(false);
	};

	const handleDrop = (event: DragEvent<HTMLDivElement>): void => {
		if (!attachmentControlsAvailable || attachmentControlsDisabled) {
			return;
		}
		event.stopPropagation();
		event.preventDefault();
		setDragActive(false);
		const files = Array.from(event.dataTransfer.files);
		if (files.length > 0) {
			onUploadFiles?.(files);
		}
	};

	const submit = (): void => {
		// Gate the Enter/submit path on the SAME conditions that disable the Send button. The button
		// respects `sendDisabled`, but the Textarea's onKeyDown calls submit() directly — so without this guard the
		// keyboard path bypasses `sendDisabledProp` (selected-conversation still loading, remote view-only thread)
		// and can fire a send the button would have refused.
		if (!trimmed || disabled || sendDisabledProp || isSending || overSizeLimit) {
			return;
		}

		const safeEffort = isEffortAvailable(reasoningEffort, availableReasoningEfforts)
			? reasoningEffort
			: (availableReasoningEfforts[0] ?? "none");
		const command = resolveSlashCommand(trimmed, commandOptions);
		onSend(command?.prompt ?? trimmed, safeEffort, selectedModel);
		setContent("");
		setSelection({ start: 0, end: 0 });
	};

	// The toolbar is hosted in the Textarea's bottomSection (rendered inside the input border, pointer-events:all —
	// so the Stop button stays interactive even while the input element itself is disabled during a send). A
	// single wrap="wrap" Group (rather than nowrap) lets the controls reflow onto extra rows instead of
	// overlapping on narrow panes; the Send button carries its own auto left-margin so it always lands at the
	// right edge of whichever row it ends up on, including a row of its own.
	const toolbar = (
		<Group ref={toolbarRef} align="center" wrap="wrap" gap="xs" style={{ width: "100%" }}>
			<Group gap={4} wrap="wrap" style={{ flex: 1, minWidth: 0 }}>
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
				{showKnowledgeBaseControls ? (
					<Tooltip
						label={
							!knowledgeBaseHasDocuments
								? t("pages.chat.knowledgeBaseNoDocuments", "No indexed documents to search")
								: knowledgeBaseEnabled
									? t("pages.chat.knowledgeBaseEnabled", "Knowledge base enabled")
									: t("pages.chat.knowledgeBaseDisabled", "Knowledge base disabled")
						}
					>
						<ActionIcon
							size={36}
							variant={knowledgeBaseEnabled && knowledgeBaseHasDocuments ? "light" : "subtle"}
							color={knowledgeBaseEnabled && knowledgeBaseHasDocuments ? "primary" : "gray"}
							// Disabled with no indexed docs: grounding on an empty corpus is a no-op. The persisted
							// enabled-preference is untouched (the store keeps it), so it re-arms once a doc is indexed.
							disabled={disabled || isSending || !onToggleKnowledgeBase || !knowledgeBaseHasDocuments}
							onClick={onToggleKnowledgeBase}
							aria-label={t("pages.chat.knowledgeBaseLabel", "Use knowledge base")}
							aria-pressed={knowledgeBaseEnabled && knowledgeBaseHasDocuments}
							data-testid="chat-knowledge-base-toggle"
						>
							<IconBooks size={15} />
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
				{attachmentControlsAvailable ? (
					<FileButton onChange={handlePickFiles} multiple={true} accept={attachmentAccept}>
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
				{showVoiceControls ? <VoiceComposerControls /> : null}
				{showContextUsage && contextUsage ? (
					<Group gap={4} wrap="nowrap">
						<ContextUsageBadge {...contextUsage} />
						<CompactButton
							percentUsed={
								contextUsage.usedTokens !== undefined && contextUsage.maxTokens !== undefined && contextUsage.maxTokens > 0
									? (contextUsage.usedTokens / contextUsage.maxTokens) * 100
									: undefined
							}
							// Read-only (e.g. remote) conversations reject the mutation with 409, and a live turn is already
							// driving the local runtime — disable compaction in both cases, mirroring the composer.
							disabled={disabled || isSending}
						/>
					</Group>
				) : null}
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
			onDragOver={attachmentControlsAvailable ? handleDragOver : undefined}
			onDragLeave={attachmentControlsAvailable ? handleDragLeave : undefined}
			onDrop={attachmentControlsAvailable ? handleDrop : undefined}
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
					/* The cloud sections of the model picker are the same list Chat.tsx derives selectedModelIsCloud
					   from, so membership here needs no extra prop threaded down. */
					cloudModelSelected={cloudModelOptions?.some((option) => option.value === selectedModel) === true}
				/>
			) : null}
			{attachmentControlsAvailable ? (
				<ChatAttachmentChips
					attachments={[...attachments]}
					pendingUploads={[...pendingUploads]}
					onRemove={onRemoveAttachment ?? (() => undefined)}
					disabled={attachmentControlsDisabled}
				/>
			) : null}
			{/* Size pre-check notice. One row covers both states: a dimmed readout once the draft passes 80% of the cap,
			    turning into the red over-limit sentence past it. It sits above the input (beside the attachment chips)
			    rather than in the bottomSection toolbar — that toolbar is already fighting for width and wraps, and the
			    over-limit sentence needs a full line to stay legible. Same tabular-nums readout style as the
			    context-usage badge, and the same <output aria-live> so a screen reader hears the state change. */}
			{sizeState ? (
				<output
					aria-live="polite"
					data-testid="composer-size-notice"
					style={{ display: "block", marginBottom: 4, textAlign: "right" }}
				>
					<Text component="span" size="xs" c={overSizeLimit ? "red" : "dimmed"} style={{ fontVariantNumeric: "tabular-nums" }}>
						{overSizeLimit
							? t(
									"pages.chat.composer.size.overLimit",
									"This message is {{used}} KB — over the {{limit}} KB limit for a single message. Shorten it, or attach the text as a file.",
									{ used: toDisplayKb(sizeState.bytes), limit: toDisplayKb(sizeState.limitBytes) },
								)
							: t("pages.chat.composer.size.usage", "{{used}} KB / {{limit}} KB", {
									used: toDisplayKb(sizeState.bytes),
									limit: toDisplayKb(sizeState.limitBytes),
								})}
					</Text>
				</output>
			) : null}
			<SlashCommandAutocomplete
				store={autocomplete.combobox}
				options={autocomplete.matches}
				activeDescendantId={autocomplete.activeDescendantId}
				onSelect={autocomplete.select}
				target={<Textarea
				ref={inputRef}
				data-testid="chat-input"
				placeholder={t("pages.chat.inputPlaceholder", "Message the local node")}
				value={content}
				onChange={(event) => {
					const target = event.currentTarget;
					setContent(target.value);
					setSelection({ start: target.selectionStart, end: target.selectionEnd });
				}}
				onSelect={(event) => setSelection({ start: event.currentTarget.selectionStart, end: event.currentTarget.selectionEnd })}
				onClick={(event) => setSelection({ start: event.currentTarget.selectionStart, end: event.currentTarget.selectionEnd })}
				onKeyUp={(event) => setSelection({ start: event.currentTarget.selectionStart, end: event.currentTarget.selectionEnd })}
				onCompositionStart={() => setIsComposing(true)}
				onCompositionEnd={(event) => {
					setIsComposing(false);
					setSelection({ start: event.currentTarget.selectionStart, end: event.currentTarget.selectionEnd });
				}}
				onKeyDown={(event) => {
					if (autocomplete.onKeyDown(event)) {
						return;
					}
					if (event.key === "Enter" && !event.shiftKey) {
						if (event.nativeEvent.isComposing || isComposing) {
							return;
						}
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
			/>}
			/>
		</Box>
	);
}
