import { z } from "zod";

import type { NodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

// The Node settings page is one draft split into sections by operator intent. The order here is the nav order.
export const nodeSettingsSectionIds = [
	"general",
	"chat",
	"runtime",
	"runtimes",
	"models",
	"knowledge",
	"voice",
	"privacy",
	"integrations",
	"workspaces",
	"usage",
] as const;

export type NodeSettingsSectionId = (typeof nodeSettingsSectionIds)[number];

export const defaultNodeSettingsSection: NodeSettingsSectionId = "general";

// Simple UI mode lists these behind "Show advanced sections". They stay reachable by URL either way.
const advancedSections: ReadonlySet<NodeSettingsSectionId> = new Set<NodeSettingsSectionId>([
	"runtime",
	"runtimes",
	"integrations",
	"workspaces",
	"usage",
]);

export function isAdvancedNodeSettingsSection(section: NodeSettingsSectionId): boolean {
	return advancedSections.has(section);
}

// `?section=` on the node-settings route. An unknown value falls back to the default section instead of failing the
// route, so an old or mistyped link still opens the page.
export const nodeSettingsSearchSchema = z.object({
	section: z.enum(nodeSettingsSectionIds).optional().catch(undefined),
});

// The one section each draft field is edited in. A Record over every form key makes a new field a compile error until
// it is placed, which is what keeps "every stored setting is reachable in exactly one section" true.
const nodeSettingsFieldSections: Readonly<
	Record<keyof NodeSettingsFieldsForm | "maxMessageRequestTimeoutSeconds", NodeSettingsSectionId>
> = {
	uiMode: "general",
	maxMessageRequestTimeoutSeconds: "chat",
	defaultModelName: "chat",
	enableTools: "chat",
	customToolsEnabled: "chat",
	toolRelevanceEnabled: "chat",
	toolCapableModels: "chat",
	autoEffortFastModelName: "chat",
	orchestrationIdleTimeoutSeconds: "chat",
	maxPendingToolCallAgeMinutes: "chat",
	detachedGraceSeconds: "chat",
	llamaMaxLoadedProcesses: "runtime",
	llamaIdleTimeToLiveSeconds: "runtime",
	keepModelWarmEnabled: "runtime",
	keepModelWarmModelName: "runtime",
	keepModelWarmIntervalSeconds: "runtime",
	kvCacheType: "runtime",
	speculativeMode: "runtime",
	speculativeDraftModelName: "runtime",
	speculativeDraftMaxTokens: "runtime",
	chatCacheReuse: "runtime",
	ollamaEndpoint: "runtimes",
	huggingFaceDefaultQuant: "models",
	rerankerModelName: "knowledge",
	webAccessEnabled: "knowledge",
	webSearchSearxngUrl: "knowledge",
	voiceFeatureEnabled: "voice",
	defaultVoiceProfile: "voice",
	externalAccessProfile: "privacy",
	autoCheckApplicationUpdates: "privacy",
	autoCheckRuntimeUpdates: "privacy",
	autoProvisionFirstRunModel: "privacy",
	agentHomePrepareTimeoutSeconds: "workspaces",
	agentHomeCommandTimeoutSeconds: "workspaces",
	agentHomeMaxSelectedFolderBytes: "workspaces",
	agentHomeMaxPatchBytes: "workspaces",
	maxResponseSizeMb: "usage",
	usageRates: "usage",
	maxProviderCallsPerInvocation: "chat",
	customToolMaxTimeoutSeconds: "chat",
	llamaReadinessTimeoutCapSeconds: "runtime",
	llamaChatHttpTimeoutSeconds: "runtime",
	llamaEmbeddingHttpTimeoutSeconds: "runtime",
	llamaChatCacheRamMode: "runtime",
	llamaChatCacheRamMiB: "runtime",
	llamaCpuThreadReserve: "runtime",
	llamaGpuReservePercent: "runtime",
	llamaRamReservePercent: "runtime",
	speculativeDraftGpuLayers: "runtime",
	imageIdleTimeToLiveSeconds: "runtime",
	modelFitSafetyMarginPercent: "runtime",
	containerRuntimeSelection: "runtimes",
	huggingFaceDownloadConnections: "models",
	huggingFaceDiskMarginBytes: "models",
	webFetchTimeoutSeconds: "knowledge",
	webFetchMaxContentChars: "knowledge",
	knowledgeSearchDefaultResults: "knowledge",
	knowledgeSearchMaxResults: "knowledge",
	transcriptionIdleTimeoutMinutes: "voice",
	transcriptionInferenceTimeoutMinutes: "voice",
	agentHomeMaxRunSeconds: "workspaces",
	agentHomeRunRetentionDays: "workspaces",
};

export function nodeSettingsSectionOf(field: string): NodeSettingsSectionId | undefined {
	return (nodeSettingsFieldSections as Readonly<Record<string, NodeSettingsSectionId>>)[field];
}
