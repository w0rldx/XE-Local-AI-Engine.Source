import type { MessageRevisionGroup } from "@/features/chat/models/MessageRevisionGrouping";

// One rendered turn in the chat list: a persisted revision group, or the transient synthetic streaming turn.
export type ListRow =
	| { readonly kind: "group"; readonly key: string; readonly group: MessageRevisionGroup }
	| { readonly kind: "streaming"; readonly key: string };
