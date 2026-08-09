import type { TFunction } from "i18next";
import type { Step } from "react-joyride";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";

export type TutorialId = "quick-start" | "agents-basics" | "knowledge-base-basics";
export type QuickStartReadiness = "ready" | "installed-unselected" | "missing" | "unresolved";

export interface TutorialDefinition {
	id: TutorialId;
	persistenceKey: string;
	stepIds: readonly string[];
	routeByStepId: Readonly<Record<string, string | undefined>>;
	isAvailable: boolean;
	estimatedMinutes: number;
}

const quickStartStepIds = [
	"navModels",
	"recommendationInstall",
	"setDefaultModel",
	"navChat",
	"chatInput",
	"chatSend",
	"firstResponse",
] as const;

const agentsStepIds = ["agentsOverview", "agentsTemplates", "agentsCreate", "agentsList"] as const;
const knowledgeStepIds = ["knowledgeOverview", "knowledgeUpload", "knowledgeDocuments", "knowledgeSearch"] as const;

export const tutorialRegistry = [
	{
		id: "quick-start",
		persistenceKey: "main-app-v1",
		stepIds: quickStartStepIds,
		routeByStepId: {
			navModels: nodeRoutePaths.models,
			recommendationInstall: nodeRoutePaths.modelRecommendations,
			setDefaultModel: nodeRoutePaths.models,
			navChat: nodeRoutePaths.chat,
			chatInput: nodeRoutePaths.chat,
			chatSend: nodeRoutePaths.chat,
			firstResponse: nodeRoutePaths.chat,
		},
		isAvailable: true,
		estimatedMinutes: 3,
	},
	{
		id: "agents-basics",
		persistenceKey: "agents-v1",
		stepIds: agentsStepIds,
		routeByStepId: Object.fromEntries(agentsStepIds.map((stepId) => [stepId, nodeRoutePaths.agents])),
		isAvailable: nodeCapabilities.agentManagement,
		estimatedMinutes: 2,
	},
	{
		id: "knowledge-base-basics",
		persistenceKey: "knowledge-base-v1",
		stepIds: knowledgeStepIds,
		routeByStepId: Object.fromEntries(knowledgeStepIds.map((stepId) => [stepId, nodeRoutePaths.knowledgeBase])),
		isAvailable: nodeCapabilities.knowledgeBase,
		estimatedMinutes: 2,
	},
] as const satisfies readonly TutorialDefinition[];

export function getTutorialDefinition(id: TutorialId): TutorialDefinition {
	const definition = tutorialRegistry.find((candidate) => candidate.id === id);
	if (!definition) {
		throw new Error(`Unknown tutorial: ${id}`);
	}
	return definition;
}

export function getQuickStartStepIds(readiness: QuickStartReadiness): readonly string[] {
	if (readiness === "ready") {
		return quickStartStepIds.slice(3);
	}
	if (readiness === "installed-unselected") {
		return quickStartStepIds.slice(2);
	}
	return quickStartStepIds;
}

const targets: Record<string, string> = {
	navModels: '[data-tour="models-overview"]',
	recommendationInstall: '[data-tour="recommendation-install"]',
	setDefaultModel: '[data-tour="set-default-model"]',
	navChat: '[data-tour="chat-overview"]',
	chatInput: '[data-testid="chat-input"]',
	chatSend: '[data-testid="chat-send-button"]',
	firstResponse: '[data-testid="chat-input-area"]',
	agentsOverview: '[data-tour="agents-overview"]',
	agentsTemplates: '[data-tour="agents-templates"]',
	agentsCreate: '[data-tour="agents-create"]',
	agentsList: '[data-tour="agents-list"]',
	knowledgeOverview: '[data-tour="knowledge-overview"]',
	knowledgeUpload: '[data-tour="knowledge-upload"]',
	knowledgeDocuments: '[data-tour="knowledge-documents"]',
	knowledgeSearch: '[data-tour="knowledge-search"]',
};

export function buildTutorialSteps(
	t: TFunction,
	definition: TutorialDefinition,
	stepIds: readonly string[],
	targetWaitTimeoutMs = 3000,
): Step[] {
	return stepIds.map((stepId) => {
		const isQuickStartAction =
			definition.id === "quick-start" &&
			["recommendationInstall", "setDefaultModel", "chatInput", "chatSend"].includes(stepId);
		return {
			target: targets[stepId] ?? "body",
			title: t(`onboarding.tutorials.${definition.id}.steps.${stepId}.title`),
			content: t(`onboarding.tutorials.${definition.id}.steps.${stepId}.content`),
			skipBeacon: true,
			// Quick Start must let the user perform the highlighted setup/chat actions. The two feature tutorials are
			// deliberately passive and therefore block every target interaction.
			blockTargetInteraction: !isQuickStartAction,
			targetWaitTimeout: targetWaitTimeoutMs,
			placement: stepId === "firstResponse" ? "top" : "auto",
		};
	});
}

export function resolveResumeStepId(
	savedStepId: string,
	allStepIds: readonly string[],
	eligibleStepIds: readonly string[],
): string {
	if (eligibleStepIds.includes(savedStepId)) {
		return savedStepId;
	}
	const savedIndex = allStepIds.indexOf(savedStepId);
	const nextEligible = allStepIds.slice(savedIndex + 1).find((stepId) => eligibleStepIds.includes(stepId));
	return nextEligible ?? eligibleStepIds[0] ?? allStepIds[0] ?? savedStepId;
}
