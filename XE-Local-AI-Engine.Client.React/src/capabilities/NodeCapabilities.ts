export interface ChatCapabilities {
	readonly localRuntime: boolean;
	readonly localModelManagement: boolean;
	readonly localTools: boolean;
	readonly toolApprovals: boolean;
	readonly conversationFeedback: boolean;
	readonly offlineFirst: boolean;
	readonly encryptedConversations: boolean;
	readonly clientNodeRouting: boolean;
	readonly fileAttachments: boolean;
	readonly imageAttachments: boolean;
}

export interface NodeCapabilityConfig {
	readonly chat: ChatCapabilities;
	readonly binding: boolean;
	readonly dashboard: boolean;
	readonly nodeSettings: boolean;
	readonly cloudSettings: boolean;
	readonly modelManagement: boolean;
	readonly runtimeManager: boolean;
	readonly invocationMonitor: boolean;
}

export const nodeCapabilities: NodeCapabilityConfig = {
	chat: {
		localRuntime: true,
		localModelManagement: true,
		// infra wired end-to-end; kept off until a local tool catalog ships — see Plans/chat-capability-gap-rc.md D6
		localTools: false,
		toolApprovals: false,
		conversationFeedback: true,
		offlineFirst: true,
		encryptedConversations: false,
		clientNodeRouting: false,
		fileAttachments: false,
		imageAttachments: false,
	},
	binding: true,
	dashboard: true,
	nodeSettings: true,
	cloudSettings: true,
	modelManagement: true,
	runtimeManager: true,
	invocationMonitor: true,
};

export const nodeRoutePaths = {
	home: "/",
	chat: "/chat",
	dashboard: "/dashboard",
	binding: "/node-binding",
	nodeSettings: "/node-settings",
	cloudSettings: "/cloud-settings",
	models: "/models",
	manager: "/manager",
	invocations: "/invocations",
} as const;

export type NodeRouteId = keyof typeof nodeRoutePaths;
export type NodeRoutePath = (typeof nodeRoutePaths)[NodeRouteId];
