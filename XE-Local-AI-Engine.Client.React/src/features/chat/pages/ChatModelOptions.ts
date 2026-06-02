import type { ModelOption } from "@/features/chat/models/ChatModels";
import type { LocalModelDto } from "@/features/models/api/LocalModelsApi";

export function toModelOption(model: LocalModelDto, nodeAvailable: boolean): ModelOption {
	const statusLabel = [
		model.isSelected ? "Node default" : undefined,
		model.parameterSize ?? undefined,
		model.quantizationLevel ?? undefined,
	]
		.filter((part): part is string => Boolean(part))
		.join(" · ");

	return {
		value: model.modelName,
		label: model.modelName,
		isReasoningModel: false,
		isAvailable: nodeAvailable,
		statusLabel: statusLabel.length > 0 ? statusLabel : undefined,
	};
}

// Strict picker filter (locked decision D3): only chat-capable models reach the composer's model selector.
// Embedding and Unknown models are hidden because they have no completion head and reject the chat endpoint.
// Lives in its own module (not Chat.tsx) so it is unit-testable and so exporting it does not break the
// component-only-export Fast Refresh rule on the page.
export function toChatModelOptions(models: LocalModelDto[], nodeAvailable: boolean): ModelOption[] {
	return models.filter((model) => model.kind === "Chat").map((model) => toModelOption(model, nodeAvailable));
}
