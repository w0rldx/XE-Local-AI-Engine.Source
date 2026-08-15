import { ApiError } from "@/core/api/errors/ApiError";
import type { ConflictProblemDetails } from "@/core/api/models/ProblemDetails";

/**
 * `conflictType` discriminator the node's global ConflictExceptionHandler writes when a mutation targets a
 * read-only (Origin=Remote) conversation. Mirrors `NodeConflictProblemType.ReadOnlyConversation`
 * (XE-Local-AI-Engine.Client/Common/ProblemDetailModels/Enums/NodeConflictProblemType.cs), which serializes as the
 * enum NAME — rename it there and this string must follow.
 */
const readOnlyConversationConflictType = "ReadOnlyConversation";

/**
 * True when the error is the node's 409 rejection of a write to a remote-origin (view-only) conversation.
 *
 * Matches the thrown `ApiError`, not an axios error: the response interceptor rethrows every non-2xx as `ApiError`,
 * so `isAxiosError` is already false by the time a component sees it.
 */
export function isNodeChatReadOnlyConflict(error: unknown): boolean {
	if (!(error instanceof ApiError) || error.statusCode !== 409) {
		return false;
	}

	const problemDetails = error.apiProblemDetails as Partial<ConflictProblemDetails> | undefined;
	return problemDetails?.conflictType === readOnlyConversationConflictType;
}
