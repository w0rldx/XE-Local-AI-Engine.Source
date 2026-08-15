import { ApiError } from "@/core/api/errors/ApiError";
import type { ConflictProblemDetails } from "@/core/api/models/ProblemDetails";

/**
 * `conflictType` discriminator the node's global ConflictExceptionHandler writes when a mutation targets a
 * read-only (Origin=Remote) conversation. Mirrors `NodeConflictProblemType.ReadOnlyConversation`
 * (XE-Local-AI-Engine.Client/Common/ProblemDetailModels/Enums/NodeConflictProblemType.cs), which serializes as the
 * enum NAME — rename it there and this string must follow. `LocalChatHub` prefixes its `HubException` message with
 * the same token, so the SignalR path is discriminated off this one constant too.
 */
const readOnlyConversationConflictType = "ReadOnlyConversation";

/**
 * SignalR wraps a server HubException as "An unexpected error occurred invoking '<method>' on the server.
 * HubException: <the actual message>" — the prefix is generic noise and the tail is the sentence the hub
 * deliberately wrote (the message-size rejection, the read-only rejection).
 */
const signalRHubErrorPrefix = /^An unexpected error occurred invoking '[^']*' on the server\.\s*HubException:\s*/;

/**
 * Strips SignalR's generic wrapper so callers read the sentence the hub actually wrote. Anything not matching the
 * wrapper passes through untouched.
 */
export function stripSignalRHubErrorPrefix(message: string): string {
	return message.replace(signalRHubErrorPrefix, "");
}

/**
 * True when the error is the node's rejection of a write to a remote-origin (view-only) conversation — over REST
 * (a 409 carrying `conflictType`) or over SignalR (a `HubException` whose message LEADS with the same token, since
 * SignalR forwards a bare string and no structured body).
 *
 * The REST branch matches the thrown `ApiError`, not an axios error: the response interceptor rethrows every non-2xx
 * as `ApiError`, so `isAxiosError` is already false by the time a component sees it.
 */
export function isNodeChatReadOnlyConflict(error: unknown): boolean {
	if (error instanceof ApiError) {
		if (error.statusCode !== 409) {
			return false;
		}

		const problemDetails = error.apiProblemDetails as Partial<ConflictProblemDetails> | undefined;
		return problemDetails?.conflictType === readOnlyConversationConflictType;
	}

	return error instanceof Error && stripSignalRHubErrorPrefix(error.message).startsWith(`${readOnlyConversationConflictType}:`);
}
