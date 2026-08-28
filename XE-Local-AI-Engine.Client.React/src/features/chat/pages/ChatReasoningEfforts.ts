import type { ModelOption, ReasoningEffort } from "@/features/chat/models/ChatModels";
import { binaryReasoningEfforts, codexReasoningEfforts, reasoningEfforts } from "@/features/chat/stores/NodeChatPreferencesStore";

// Which reasoning-effort vocabulary the composer offers for the active model. Lives beside the page rather than in it
// (like shouldFetchLocalModelDetails) so the rule is unit-testable without rendering the chat.
//
// - Cloud (Codex) models get the full OpenAI Responses vocabulary: none/minimal/low/medium/high/xhigh. "minimal" and
//   "xhigh" are Codex-only and must NEVER be offered for a local model.
// - A model advertising the Ollama `thinking` capability gets the graded set: none/low/medium/high.
// - Everything else (native-reasoning, non-thinking Ollama, the local default) gets binary On/Off: on/none. "On"
//   omits `think` so a model that reasons by default runs its built-in reasoning; "Off" sends think:false.
//
// NATIVE-reasoning models (harmony/gpt-oss, `isNativeReasoningModel`) belong in the BINARY bucket on purpose and are
// deliberately absent from the graded branch. They reason on a template-baked channel with no graded switch, so
// none/low/medium/high would be a menu whose levels do nothing — and routing them through the graded path would send
// an `enable_thinking=false` their template has no kwarg for, breaking reasoning-off outright. Their capability is
// surfaced by the picker BADGE instead.
//
// An externally served model that reasons but declared no graded effort joins that same binary bucket, for the same
// reason: its endpoint ignores `reasoning_effort`. Only an external connection declares the field at all — every
// other provider reports null, which arrives here as undefined and leaves the graded path exactly as it was.
export function resolveAvailableReasoningEfforts(option: ModelOption | undefined): ReasoningEffort[] {
	if (option?.isCloud === true) {
		return [...codexReasoningEfforts];
	}

	if (option?.isReasoningModel === true && option.isReasoningEffortCapable !== false) {
		return [...reasoningEfforts];
	}

	return [...binaryReasoningEfforts];
}
