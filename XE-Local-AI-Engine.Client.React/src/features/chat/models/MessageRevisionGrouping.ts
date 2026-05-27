import type { ChatMessageModel } from "@/features/chat/models/ChatModels";

/**
 * A displayed message together with its sibling revisions. Assistant turns regenerated via the shared runner
 * are stored as sibling variants sharing a `variantGroupId` (never in-place overwrite — Phase 5.2). The chat
 * surface collapses each variant group to ONE visible message and lets the operator page across the siblings.
 */
export interface MessageRevisionGroup {
	/** The message currently shown for this group (the active revision). */
	readonly active: ChatMessageModel;
	/** Every revision in the group, oldest first. Length 1 when the message has no siblings. */
	readonly revisions: ChatMessageModel[];
	/** Zero-based index of `active` within `revisions`. */
	readonly activeIndex: number;
}

function bySortOrder(left: ChatMessageModel, right: ChatMessageModel): number {
	return left.sortOrder - right.sortOrder || left.createdAt.localeCompare(right.createdAt);
}

/**
 * Collapses variant groups in a sorted message list to one entry per group, honoring an optional per-group
 * active-revision selection (keyed by `variantGroupId`). Messages without a `variantGroupId` pass through as
 * singleton groups. The returned list preserves the original ordering anchored on each group's earliest member.
 */
export function groupMessageRevisions(
	messages: ChatMessageModel[],
	activeRevisionByGroup: Readonly<Record<string, string>> = {},
): MessageRevisionGroup[] {
	const ordered = messages.toSorted(bySortOrder);
	const groupsById = new Map<string, ChatMessageModel[]>();
	const result: MessageRevisionGroup[] = [];

	for (const message of ordered) {
		const groupId = message.variantGroupId;
		if (!groupId) {
			result.push({ active: message, revisions: [message], activeIndex: 0 });
			continue;
		}

		const existing = groupsById.get(groupId);
		if (existing) {
			existing.push(message);
			continue;
		}

		const revisions: ChatMessageModel[] = [message];
		groupsById.set(groupId, revisions);
		// Reserve the slot now (anchored at the earliest member); the active selection is resolved below.
		result.push({ active: message, revisions, activeIndex: 0 });
	}

	return result.map((group) => {
		const groupId = group.active.variantGroupId;
		if (!groupId || group.revisions.length <= 1) {
			return group;
		}

		const revisions = group.revisions.toSorted(bySortOrder);
		const requestedId = activeRevisionByGroup[groupId];
		const requestedIndex = requestedId ? revisions.findIndex((revision) => revision.id === requestedId) : -1;
		// Default to the newest revision so a fresh regeneration surfaces without an explicit selection.
		const activeIndex = requestedIndex >= 0 ? requestedIndex : revisions.length - 1;
		return { active: revisions[activeIndex] ?? group.active, revisions, activeIndex };
	});
}
