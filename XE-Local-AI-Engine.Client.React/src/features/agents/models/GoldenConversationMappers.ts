import type {
	XeLocalAiEngineClientEndpointsAgentsV1CreateGoldenConversationRequest,
	XeLocalAiEngineClientEndpointsAgentsV1GoldenConversationResponse,
	XeLocalAiEngineClientEndpointsAgentsV1GoldenHarvestResponse,
	XeLocalAiEngineClientEndpointsAgentsV1ListGoldenConversationsResponse,
} from "@/core/api/generated";
import type {
	CreateGoldenConversationRequestDto,
	GoldenConversation,
	GoldenConversationSource,
	GoldenHarvestResult,
} from "@/features/agents/models/GoldenConversationModels";

// Maps the generated (OpenAPI) golden-conversation responses to the stricter domain view-models the panel depends
// on. The generated types are the single source of truth for the wire shape; their fields are all optional (`x?: T`),
// so each mapper coalesces every field to a required value with a safe default. Boundary validation and ApiError
// convergence are owned by the generated zod validator (`validator: true`) + the withResponseValidation bridge at
// the hook — these mappers only project the already-validated wire shape into the immutable domain shape (they no
// longer re-validate, replacing the feature's former hand-zod safeParse). Free text is encrypted at rest on the
// node; the wire shape is the decrypted camelCase view. Redaction is the backend's: the mappers surface only what
// the response carries (harvested-but-disabled cases are the pending-review set; INERT until approved server-side).

// `source` is the provenance discriminator. The generated type widens it to `string`; anything that is not the
// known "harvested" literal degrades to "manual" so the domain union stays total (matching the former `.catch`).
function toSource(source: string | undefined): GoldenConversationSource {
	return source === "harvested" ? "harvested" : "manual";
}

export function toGoldenConversation(dto: XeLocalAiEngineClientEndpointsAgentsV1GoldenConversationResponse): GoldenConversation {
	return {
		id: dto.id ?? "",
		agentDefinitionId: dto.agentDefinitionId ?? "",
		title: dto.title ?? "",
		inputTurns: (dto.inputTurns ?? []).map((turn) => ({ role: turn.role ?? "", text: turn.text ?? "" })),
		assertion: dto.assertion
			? {
					requiredPhrases: [...(dto.assertion.requiredPhrases ?? [])],
					forbiddenPhrases: [...(dto.assertion.forbiddenPhrases ?? [])],
				}
			: null,
		rubric: dto.rubric ?? null,
		enabled: dto.enabled ?? false,
		source: toSource(dto.source),
		sourceMessageId: dto.sourceMessageId ?? null,
		sourceConversationId: dto.sourceConversationId ?? null,
		createdAtUtc: dto.createdAtUtc ?? 0,
		updatedAtUtc: dto.updatedAtUtc ?? 0,
	};
}

export function toGoldenConversations(
	dto: XeLocalAiEngineClientEndpointsAgentsV1ListGoldenConversationsResponse,
): GoldenConversation[] {
	return (dto.items ?? []).map(toGoldenConversation);
}

export function toGoldenHarvestResult(dto: XeLocalAiEngineClientEndpointsAgentsV1GoldenHarvestResponse): GoldenHarvestResult {
	return {
		thumbsUpScanned: dto.thumbsUpScanned ?? 0,
		createdCount: dto.createdCount ?? 0,
		duplicateCount: dto.duplicateCount ?? 0,
		skippedCount: dto.skippedCount ?? 0,
	};
}

// Projects the domain create request (immutable/readonly arrays) onto the generated request body (mutable arrays).
// The generated body fields are optional; the domain shape supplies the required ones. An absent assertion is sent
// as null (the generated type accepts null), and an absent rubric coalesces to null so a blank field never carries
// a stale value.
export function toCreateGoldenConversationRequest(
	dto: CreateGoldenConversationRequestDto,
): XeLocalAiEngineClientEndpointsAgentsV1CreateGoldenConversationRequest {
	return {
		title: dto.title,
		inputTurns: dto.inputTurns.map((turn) => ({ role: turn.role, text: turn.text })),
		assertion: dto.assertion
			? {
					requiredPhrases: [...dto.assertion.requiredPhrases],
					forbiddenPhrases: [...dto.assertion.forbiddenPhrases],
				}
			: null,
		rubric: dto.rubric ?? null,
		enabled: dto.enabled,
	};
}
