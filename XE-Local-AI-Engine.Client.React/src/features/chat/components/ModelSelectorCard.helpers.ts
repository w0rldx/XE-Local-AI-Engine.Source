import type { ModelOption } from "@/features/chat/models/ChatModels";

export function cx(...names: (string | false | undefined)[]): string {
	return names.filter(Boolean).join(" ");
}

export function display(option: ModelOption | undefined, fallback: string): string {
	return option?.displayName?.trim() || option?.label.trim() || option?.value.trim() || fallback;
}
