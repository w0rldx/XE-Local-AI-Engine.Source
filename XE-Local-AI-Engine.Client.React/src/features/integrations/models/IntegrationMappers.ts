import type {
	XeLocalAiEngineClientEndpointsIntegrationsV1CreateIntegrationTriggerRequest,
	XeLocalAiEngineClientEndpointsIntegrationsV1GenerateIntegrationApiKeyRequest,
	XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationApiKeyView,
	XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationExecutionDetailDto,
	XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationExecutionEventDto,
	XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationExecutionSummaryDto,
	XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationSessionResponse,
	XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationTriggerView,
	XeLocalAiEngineClientEndpointsIntegrationsV1UpdateIntegrationTriggerRequest,
} from "@/core/api/generated";
import type {
	IntegrationApiKey,
	IntegrationExecution,
	IntegrationExecutionDetail,
	IntegrationExecutionEvent,
	IntegrationInputKind,
	IntegrationKeyFormValues,
	IntegrationSession,
	IntegrationSessionPolicy,
	IntegrationTrigger,
	IntegrationTriggerFormValues,
} from "@/features/integrations/models/IntegrationModels";

// DTO ⇄ domain mapping for the integrations admin surface. Wire validation stays at the API boundary (the generated
// zod response validator); these functions only narrow optional wire fields into total domain values.

const DEFAULT_SESSION_POLICY: IntegrationSessionPolicy = "PerInvocation";

// The wire carries lowercase member names ("text", "json"), read case-insensitively by the backend. An unknown
// member is dropped rather than widened: the two the client understands are the two the checkbox group offers.
function toInputKinds(names: readonly string[]): IntegrationInputKind[] {
	const kinds: IntegrationInputKind[] = [];
	if (names.some((name) => name.toLowerCase() === "text")) {
		kinds.push("text");
	}
	if (names.some((name) => name.toLowerCase() === "json")) {
		kinds.push("json");
	}
	return kinds;
}

export function toIntegrationTrigger(dto: XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationTriggerView): IntegrationTrigger {
	return {
		id: dto.id,
		name: dto.name,
		displayName: dto.displayName,
		description: dto.description ?? "",
		enabled: dto.enabled,
		targetAgentDefinitionId: dto.targetAgentDefinitionId,
		sessionPolicy: dto.sessionPolicy ?? DEFAULT_SESSION_POLICY,
		acceptedInputKinds: toInputKinds(dto.acceptedInputKinds),
		createdAtUtc: dto.createdAtUtc,
		updatedAtUtc: dto.updatedAtUtc,
		version: dto.version,
	};
}

export function toIntegrationApiKey(dto: XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationApiKeyView): IntegrationApiKey {
	return {
		id: dto.id,
		principalId: dto.principalId,
		keyPrefix: dto.keyPrefix,
		label: dto.label,
		// null (and an absent field) is the "all triggers" wildcard; an empty array would be a key that may invoke
		// nothing, so the two are NOT collapsed here.
		allowedTriggerIds: dto.allowedTriggerIds ?? null,
		createdAtUtc: dto.createdAtUtc,
		lastUsedAtUtc: dto.lastUsedAtUtc ?? null,
		revokedAtUtc: dto.revokedAtUtc ?? null,
	};
}

function toWireInputKinds(values: IntegrationTriggerFormValues): string[] {
	const kinds: string[] = [];
	if (values.acceptsText) {
		kinds.push("text");
	}
	if (values.acceptsJson) {
		kinds.push("json");
	}
	return kinds;
}

export function toCreateIntegrationTriggerRequest(
	values: IntegrationTriggerFormValues,
): XeLocalAiEngineClientEndpointsIntegrationsV1CreateIntegrationTriggerRequest {
	return {
		name: values.name.trim(),
		displayName: values.displayName.trim(),
		description: values.description.trim() === "" ? null : values.description.trim(),
		enabled: values.enabled,
		targetKind: "Agent",
		targetAgentDefinitionId: values.targetAgentDefinitionId,
		sessionPolicy: values.sessionPolicy,
		acceptedInputKinds: toWireInputKinds(values),
	};
}

// The slug is immutable after creation (it is the integrator's URL), so the update body carries no `name`.
export function toUpdateIntegrationTriggerRequest(
	values: IntegrationTriggerFormValues,
	expectedVersion: number,
): XeLocalAiEngineClientEndpointsIntegrationsV1UpdateIntegrationTriggerRequest {
	return {
		displayName: values.displayName.trim(),
		description: values.description.trim() === "" ? null : values.description.trim(),
		enabled: values.enabled,
		targetAgentDefinitionId: values.targetAgentDefinitionId,
		sessionPolicy: values.sessionPolicy,
		acceptedInputKinds: toWireInputKinds(values),
		expectedVersion,
	};
}

export function toIntegrationTriggerFormValues(trigger: IntegrationTrigger): IntegrationTriggerFormValues {
	return {
		name: trigger.name,
		displayName: trigger.displayName,
		description: trigger.description,
		enabled: trigger.enabled,
		targetAgentDefinitionId: trigger.targetAgentDefinitionId,
		sessionPolicy: trigger.sessionPolicy,
		acceptsText: trigger.acceptedInputKinds.includes("text"),
		acceptsJson: trigger.acceptedInputKinds.includes("json"),
	};
}

/**
 * The allowlist takes the SWITCH state as its input, never the array length: mapping an empty selection to `null`
 * would turn "I picked nothing yet" into "this key may invoke every trigger on the node, including ones created
 * later". The validator guarantees the array is non-empty whenever allowAllTriggers is off.
 *
 * An empty `principalId` is the "New identity" default and sends NO principalId, so the backend mints one; any other
 * value reuses an existing integrator identity, which is how a credential is rotated without stranding its sessions.
 */
export function toGenerateIntegrationApiKeyRequest(
	values: IntegrationKeyFormValues,
): XeLocalAiEngineClientEndpointsIntegrationsV1GenerateIntegrationApiKeyRequest {
	return {
		label: values.label.trim(),
		allowedTriggerIds: values.allowAllTriggers ? null : [...values.allowedTriggerIds],
		...(values.principalId === "" ? {} : { principalId: values.principalId }),
	};
}

export function toIntegrationExecution(
	dto: XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationExecutionSummaryDto,
): IntegrationExecution {
	return {
		id: dto.id,
		triggerId: dto.triggerId,
		sessionId: dto.sessionId,
		status: dto.status,
		receivedAtUtc: dto.receivedAtUtc,
		// A run cancelled or failed before it took the lease never started, so both of these are legitimately absent
		// and render as a dash rather than as an error.
		startedAtUtc: dto.startedAtUtc ?? null,
		endedAtUtc: dto.endedAtUtc ?? null,
		failureCategory: dto.failureCategory ?? null,
		failureSummary: dto.failureSummary ?? null,
		outputCount: dto.outputCount,
	};
}

export function toIntegrationExecutionDetail(
	dto: XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationExecutionDetailDto,
): IntegrationExecutionDetail {
	return {
		execution: toIntegrationExecution(dto.execution),
		principalId: dto.principalId,
		keyPrefix: dto.keyPrefix,
		requestId: dto.requestId,
		invocationId: dto.invocationId,
		outputBytes: dto.outputBytes,
		stopRequestedAtUtc: dto.stopRequestedAtUtc ?? null,
	};
}

export function toIntegrationExecutionEvent(
	dto: XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationExecutionEventDto,
): IntegrationExecutionEvent {
	return {
		sequence: dto.sequence,
		eventType: dto.eventType,
		detailJson: dto.detailJson ?? null,
		occurredAtUtc: dto.occurredAtUtc,
	};
}

export function toIntegrationSession(
	dto: XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationSessionResponse,
): IntegrationSession {
	return {
		id: dto.id,
		triggerId: dto.triggerId,
		triggerName: dto.triggerName,
		principalId: dto.principalId,
		agentDefinitionId: dto.agentDefinitionId,
		status: dto.status,
		createdAtUtc: dto.createdAtUtc,
		lastActivityUtc: dto.lastActivityUtc,
		executionCount: dto.executionCount,
	};
}
