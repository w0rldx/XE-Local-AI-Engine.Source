// The feature's one fixture file (P4 §6). Per-DTO builders rather than per-route on purpose: the run
// payload backs four test files from one builder, parameterised by node status, so the suite needs about seven
// builders rather than one hand-rolled handler set per endpoint.
//
// Every builder returns the WIRE shape (all fields optional, as hey-api types them from the OpenAPI document), so a
// test can spread overrides in without fighting a stricter local type than the server actually promises.

import type {
	DevWorkflowArtifactResponse as ArtifactResponse,
	DevWorkflowNodeRunDetailResponse as NodeRunDetailResponse,
	DevWorkflowNodeRunSummaryResponse as NodeRunSummaryResponse,
	DevWorkflowRuleSetResponse as RuleSetResponse,
	DevWorkflowRuleSetSummaryResponse as RuleSetSummaryResponse,
	DevWorkflowRunEventResponse as RunEventResponse,
	DevWorkflowRunResponse as RunResponse,
	DevWorkflowRunSummaryResponse as RunSummaryResponse,
	DevWorkflowWorkItemResponse as WorkItemResponse,
	DevWorkflowWorkItemSummaryResponse as WorkItemSummaryResponse,
} from "@/features/devWorkflows/models/DevWorkflowModels";

export const devWorkflowTestIds = {
	workItem: "11111111-1111-4111-8111-111111111111",
	run: "22222222-2222-4222-8222-222222222222",
	definition: "33333333-3333-4333-8333-333333333333",
	nodeRun: "44444444-4444-4444-8444-444444444444",
	artifact: "55555555-5555-4555-8555-555555555555",
	ruleSet: "99999999-9999-4999-8999-999999999999",
} as const;

export function devWorkflowWorkItemSummary(overrides: Partial<WorkItemSummaryResponse> = {}): WorkItemSummaryResponse {
	return {
		id: devWorkflowTestIds.workItem,
		title: "Survey the vector-store options",
		developmentProjectId: null,
		status: "Active",
		latestRunId: devWorkflowTestIds.run,
		latestRunStatus: "Running",
		definitionName: "Research → Plan → Approval",
		queuedNodeCount: 1,
		runningNodeCount: 1,
		completedNodeCount: 1,
		totalNodeCount: 3,
		updatedAtUtc: 1_700_000_000_000,
		...overrides,
	};
}

/** Reached through `devWorkflowWorkItem`'s embedded `runs[]`, which is where the detail page reads it — including
 * whether ANY run of the item is still live, so a test needs to build a second, older row of its own. */
export function devWorkflowRunSummary(overrides: Partial<RunSummaryResponse> = {}): RunSummaryResponse {
	return {
		id: devWorkflowTestIds.run,
		workItemId: devWorkflowTestIds.workItem,
		definitionId: devWorkflowTestIds.definition,
		definitionName: "Research → Plan → Approval",
		status: "Running",
		queuedNodeCount: 1,
		runningNodeCount: 1,
		completedNodeCount: 1,
		totalNodeCount: 3,
		pendingDecisionCount: 0,
		blockingGateNodeRunId: null,
		startedAtUtc: 1_700_000_000_000,
		updatedAtUtc: 1_700_000_100_000,
		...overrides,
	};
}

export function devWorkflowWorkItem(overrides: Partial<WorkItemResponse> = {}): WorkItemResponse {
	return {
		id: devWorkflowTestIds.workItem,
		title: "Survey the vector-store options",
		request: "Compare the options and propose one.",
		developmentProjectId: null,
		status: "Active",
		latestRunId: devWorkflowTestIds.run,
		runs: [devWorkflowRunSummary()],
		createdAtUtc: 1_700_000_000_000,
		updatedAtUtc: 1_700_000_100_000,
		version: 1,
		...overrides,
	};
}

export function devWorkflowNodeRunSummary(overrides: Partial<NodeRunSummaryResponse> = {}): NodeRunSummaryResponse {
	return {
		id: devWorkflowTestIds.nodeRun,
		nodeKey: "research",
		nodeType: "Agent",
		label: "Research",
		status: "Running",
		attempt: 1,
		maxAttempts: 3,
		queueReason: null,
		queuedAtUtc: null,
		waitingOnNodeKeys: null,
		pendingDecisionKind: null,
		isMaterialized: false,
		materializedFromNodeKey: null,
		materializationIndex: null,
		materializationGroupId: null,
		materializationCount: null,
		developmentProjectId: null,
		developmentTaskId: null,
		agentDefinitionId: null,
		agentDisplayName: "Researcher",
		modelLabel: "qwen3-30b",
		hasStaleInputs: false,
		startedAtUtc: 1_700_000_000_000,
		completedAtUtc: null,
		sequence: 2,
		validationNotApplicable: false,
		...overrides,
	};
}

export function devWorkflowRun(overrides: Partial<RunResponse> = {}): RunResponse {
	return {
		id: devWorkflowTestIds.run,
		workItemId: devWorkflowTestIds.workItem,
		definitionId: devWorkflowTestIds.definition,
		definitionVersion: 1,
		definitionName: "Research → Plan → Approval",
		graphRevision: 1,
		graph: { schemaVersion: 1, nodes: [], edges: [] },
		status: "Running",
		nodes: [devWorkflowNodeRunSummary()],
		queuedNodeCount: 0,
		runningNodeCount: 1,
		pendingDecisionCount: 0,
		blockingGateNodeRunId: null,
		failureClass: null,
		terminalReason: null,
		startedAtUtc: 1_700_000_000_000,
		completedAtUtc: null,
		version: 3,
		lastSequence: 9,
		...overrides,
	};
}

export function devWorkflowNodeRunDetail(overrides: Partial<NodeRunDetailResponse> = {}): NodeRunDetailResponse {
	return {
		...devWorkflowNodeRunSummary(),
		runId: devWorkflowTestIds.run,
		sessionResumes: 0,
		workSessionId: null,
		conversationId: null,
		workSessionAvailable: false,
		primaryArtifactId: null,
		instructions: null,
		inputJson: null,
		outputJson: null,
		producedArtifactIds: [],
		consumedArtifactIds: [],
		appliedRuleSets: [],
		allowedDecisions: [],
		hasRejectBranch: false,
		failureClass: null,
		terminalReason: null,
		// Explicit rather than absent: the default node run warmed no local runtime, which is not a zero-length warm.
		modelReadinessMs: null,
		decisions: [],
		...overrides,
	};
}

export function devWorkflowRunEvent(overrides: Partial<RunEventResponse> = {}): RunEventResponse {
	return {
		id: "event-1",
		// Sequences are strictly increasing but NOT contiguous — the counter is shared with node-runs and artifacts.
		sequence: 7,
		eventType: "node.started",
		nodeRunId: devWorkflowTestIds.nodeRun,
		outcome: null,
		detailJson: null,
		operationId: null,
		occurredAtUtc: 1_700_000_000_000,
		...overrides,
	};
}

export function devWorkflowArtifact(overrides: Partial<ArtifactResponse> = {}): ArtifactResponse {
	return {
		id: devWorkflowTestIds.artifact,
		lineageId: "66666666-6666-4666-8666-666666666666",
		version: 1,
		sequence: 8,
		kind: "Research",
		name: "vector-store-survey.md",
		mediaType: "text/markdown",
		contentSha256: "0".repeat(64),
		sizeBytes: 1024,
		producedByNodeRunId: devWorkflowTestIds.nodeRun,
		producingNodeKey: "research",
		isValid: true,
		isStale: false,
		staleBecauseArtifactId: null,
		staleReason: null,
		isLatest: true,
		createdAtUtc: 1_700_000_050_000,
		...overrides,
	};
}

/** The catalogue row: everything but the body, which only the single-rule-set read carries. */
export function devWorkflowRuleSetSummary(overrides: Partial<RuleSetSummaryResponse> = {}): RuleSetSummaryResponse {
	return {
		id: devWorkflowTestIds.ruleSet,
		name: "House style",
		description: "How this repository wants its patches written.",
		scope: { projectIds: [], nodeTypes: [] },
		enabled: true,
		contentSha256: "a".repeat(64),
		version: 1,
		createdAtUtc: 1_700_000_000_000,
		updatedAtUtc: 1_700_000_100_000,
		...overrides,
	};
}

export function devWorkflowRuleSet(overrides: Partial<RuleSetResponse> = {}): RuleSetResponse {
	return {
		...devWorkflowRuleSetSummary(),
		body: "# House style\n\nSmall diffs. No new dependencies.",
		...overrides,
	};
}
