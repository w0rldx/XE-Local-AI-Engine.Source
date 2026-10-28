export const localDefaultModelValue = "local-default";

export function toNodeChatRequestModel(model: string): string | undefined {
	const trimmed = model.trim();
	return trimmed.length > 0 && trimmed !== localDefaultModelValue ? trimmed : undefined;
}
