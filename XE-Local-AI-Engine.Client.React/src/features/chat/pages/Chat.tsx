import { Container } from "@mantine/core";
import { useState } from "react";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { ChatDisplayShell } from "@/features/chat/components/ChatDisplayShell";
import { buildChatUiCapabilities, hiddenChatSurfaceLabels } from "@/features/chat/models/ChatCapabilityGates";
import type { ChatConversationModel, ChatTimelineEntry, ModelOption, ReasoningEffort } from "@/features/chat/models/ChatModels";

const createdAt = "2026-05-23T08:30:00.000Z";

const conversations: ChatConversationModel[] = [
	{
		id: "local-preview",
		title: "Local runtime preview",
		createdAt,
		updatedAt: "2026-05-23T08:40:00.000Z",
		lastActivity: "2026-05-23T08:40:00.000Z",
		lastMessagePreview: "Display-only shell with markdown, reasoning, and tool activity.",
		isPinned: true,
		messages: [
			{
				id: "msg-user-1",
				conversationId: "local-preview",
				role: "user",
				content: "Show the copied chat display running in the node client. Include **markdown**, a tool event, and reasoning.",
				status: "completed",
				createdAt: "2026-05-23T08:31:00.000Z",
				sortOrder: 1,
			},
			{
				id: "msg-assistant-1",
				conversationId: "local-preview",
				role: "assistant",
				content:
					"This node-owned display slice now renders in isolation.\n\n- Markdown and GFM lists render locally.\n- Tool activity is presentational only.\n- Sending stays disabled until Phase 4.7 wiring.\n\n```ts\nconst phase = \"4.1-display\";\n```",
				reasoning: "Keep this slice local to the node React client and avoid platform transport, storage, encryption, and client-node routing dependencies.",
				status: "completed",
				createdAt: "2026-05-23T08:32:00.000Z",
				sortOrder: 2,
				model: "llama3.2:latest",
			},
		],
	},
	{
		id: "adapter-next",
		title: "Adapter wiring next",
		createdAt: "2026-05-23T08:35:00.000Z",
		updatedAt: "2026-05-23T08:38:00.000Z",
		lastActivity: "2026-05-23T08:38:00.000Z",
		lastMessagePreview: "REST and local SignalR adapter work remains future scope.",
		messages: [
			{
				id: "msg-user-2",
				conversationId: "adapter-next",
				role: "user",
				content: "Can this send yet?",
				status: "completed",
				createdAt: "2026-05-23T08:35:00.000Z",
				sortOrder: 1,
			},
			{
				id: "msg-assistant-2",
				conversationId: "adapter-next",
				role: "assistant",
				content: "Not yet. This page deliberately uses local mock data until the node adapter, FastEndpoints API, and local SignalR stream are implemented.",
				status: "completed",
				createdAt: "2026-05-23T08:36:00.000Z",
				sortOrder: 2,
			},
		],
	},
];

const modelOptions: ModelOption[] = [
	{
		value: "llama3.2:latest",
		label: "llama3.2:latest",
		displayName: "Llama 3.2 Local",
		isReasoningModel: false,
		isAvailable: true,
		statusLabel: "Sample local model",
	},
	{
		value: "qwen3:latest",
		label: "qwen3:latest",
		displayName: "Qwen 3 Local",
		isReasoningModel: true,
		isAvailable: true,
		statusLabel: "Sample reasoning model",
	},
];

const timelineEntries: ChatTimelineEntry[] = [
	{
		id: "tool-1",
		messageId: "msg-assistant-1",
		type: "ToolCall",
		toolName: "local_context_snapshot",
		toolArgs: JSON.stringify({ scope: "node-client-chat-display" }),
		state: "received",
		createdAt: "2026-05-23T08:32:30.000Z",
	},
	{
		id: "tool-2",
		messageId: "msg-assistant-1",
		type: "ToolResult",
		toolName: "local_context_snapshot",
		toolResult: JSON.stringify({ status: "mock", importedPlatformRuntime: false }),
		state: "received",
		createdAt: "2026-05-23T08:32:32.000Z",
	},
];

const chatUiCapabilities = buildChatUiCapabilities(nodeCapabilities.chat);
const hiddenNodeSurfaces = hiddenChatSurfaceLabels(chatUiCapabilities).join(", ");
const phase42Notice = `Display preview only. Sending and model changes are disabled until the local chat adapter is wired. Node mode hides ${hiddenNodeSurfaces}.`;

export function Chat() {
	const [selectedConversationId, setSelectedConversationId] = useState(conversations[0]?.id ?? "");
	const [collapsed, setCollapsed] = useState(false);
	const [selectedModel] = useState(modelOptions[0]?.value ?? "");
	const [reasoningEffort, setReasoningEffort] = useState<ReasoningEffort>("medium");

	return (
		<Container size="xl" py="lg" h="calc(100vh - 96px)">
			<ChatDisplayShell
				conversations={conversations}
				selectedConversationId={selectedConversationId}
				modelOptions={modelOptions}
				selectedModel={selectedModel}
				reasoningEffort={reasoningEffort}
				availableReasoningEfforts={["none", "low", "medium", "high"]}
				capabilities={chatUiCapabilities}
				contextUsage={{
					usedTokens: 1380,
					maxTokens: 8192,
					isAuthoritative: false,
					modelLabel: "Llama 3.2 Local",
					nodeLabel: "Local node",
				}}
				disabledNotice={phase42Notice}
				timelineEntries={timelineEntries}
				inputStatus={{
					isSending: false,
					chatInputDisabled: true,
					modelSelectorDisabled: true,
					sendDisabled: true,
				}}
				conversationListCollapsed={collapsed}
				onSelectConversation={setSelectedConversationId}
				onCreateConversation={() => undefined}
				onToggleConversationList={() => setCollapsed((value) => !value)}
				onModelChange={() => undefined}
				onReasoningEffortChange={setReasoningEffort}
				onSend={() => undefined}
				onCancel={() => undefined}
			/>
		</Container>
	);
}
