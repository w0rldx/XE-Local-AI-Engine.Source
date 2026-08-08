import { isAxiosError } from "axios";

/** Conflict code the node returns when a mutation targets a read-only (Origin=Remote) conversation. */
const nodeChatReadOnlyConflictCode = "conversation-read-only";

interface NodeChatConflictResponseDto {
	code: string;
	reason: string;
}

/** True when the error is the node's 409 rejection of a write to a remote-origin (view-only) conversation. */
export function isNodeChatReadOnlyConflict(error: unknown): boolean {
	if (!isAxiosError(error) || error.response?.status !== 409) {
		return false;
	}

	const body = error.response.data as Partial<NodeChatConflictResponseDto> | undefined;
	return body?.code === nodeChatReadOnlyConflictCode;
}
