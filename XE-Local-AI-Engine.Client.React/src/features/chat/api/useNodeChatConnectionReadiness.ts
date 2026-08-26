import { useCallback, useEffect, useRef, useState } from "react";

import { nodeChatConnection } from "@/features/chat/api/NodeChatConnection";

export type NodeChatConnectionReadiness = "connecting" | "ready" | "error";

export interface NodeChatConnectionReadinessState {
	readonly readiness: NodeChatConnectionReadiness;
	readonly error: string | undefined;
	readonly retry: () => void;
}

// Guarded rather than a raw `error.message` read. An Error can carry an EMPTY message — `new Error()` does, and the
// shared axios interceptor's NetworkError deliberately does so renderers fall through to their own localized copy —
// and the chat gate renders this value as `connectionError ?? fallback`, which an empty string SATISFIES. The result
// was a blank "Local chat unavailable" panel on the app's main screen. Anything without usable text now falls through
// to the generic sentence below, so the panel always says something.
function describeConnectionError(error: unknown): string {
	if (error instanceof Error && error.message.trim().length > 0) {
		return error.message;
	}

	if (typeof error === "string" && error.trim().length > 0) {
		return error;
	}

	return "Unable to connect to the local chat hub.";
}

/**
 * Eager-connects the shared chat hub on mount and reports a
 * readiness state the page uses to block render until the hub is live. The hub is otherwise lazily started on
 * first send; warming it here surfaces connection failures up front instead of on the first message.
 *
 * Once connected at least once the gate latches `ready` and never downgrades on transient reconnects — those
 * are handled in-band by withAutomaticReconnect, so re-blocking the whole chat mid-session would be jarring.
 *
 * React 18 StrictMode double-invokes this effect in dev (mount → cleanup → mount). That is safe here and
 * accepted as-is: the cleanup trips `cancelledRef`, so the discarded first run's late `ensureConnection`
 * rejection and any status callbacks are dropped, and `connect()` is idempotent (the `hasConnectedRef` guard
 * makes the second invoke a no-op once the shared singleton connection is already live).
 */
export function useNodeChatConnectionReadiness(): NodeChatConnectionReadinessState {
	const [readiness, setReadiness] = useState<NodeChatConnectionReadiness>(() =>
		nodeChatConnection.status === "connected" ? "ready" : "connecting",
	);
	const [error, setError] = useState<string | undefined>(undefined);
	const hasConnectedRef = useRef(nodeChatConnection.status === "connected");
	// Guards against post-unmount state writes: the effect cleanup sets this true, after which the in-flight
	// ensureConnection rejection and any onStatusChange callback from the discarded subscription are ignored.
	const cancelledRef = useRef(false);

	// Imperative connect, reused by the mount effect and by retry(). Stable so it doesn't re-arm the effect.
	const connect = useCallback(() => {
		if (hasConnectedRef.current) {
			setReadiness("ready");
			return;
		}

		setReadiness("connecting");
		setError(undefined);
		nodeChatConnection.ensureConnection().catch((connectionError: unknown) => {
			if (cancelledRef.current || hasConnectedRef.current) {
				return;
			}

			setError(describeConnectionError(connectionError));
			setReadiness("error");
		});
	}, []);

	useEffect(() => {
		cancelledRef.current = false;

		const unsubscribe = nodeChatConnection.subscribe({
			onStatusChange: (status) => {
				if (cancelledRef.current) {
					return;
				}

				if (status === "connected") {
					hasConnectedRef.current = true;
					setError(undefined);
					setReadiness("ready");
				} else if (!hasConnectedRef.current) {
					setReadiness("connecting");
				}
			},
		});

		connect();

		return () => {
			cancelledRef.current = true;
			unsubscribe();
		};
	}, [connect]);

	return { readiness, error, retry: connect };
}
