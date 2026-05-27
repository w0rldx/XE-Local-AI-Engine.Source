import { useCallback, useEffect, useRef, useState } from "react";

import { nodeChatConnection } from "@/features/chat/api/NodeChatConnection";

export type NodeChatConnectionReadiness = "connecting" | "ready" | "error";

export interface NodeChatConnectionReadinessState {
	readonly readiness: NodeChatConnectionReadiness;
	readonly error: string | undefined;
	readonly retry: () => void;
}

function describeConnectionError(error: unknown): string {
	if (error instanceof Error) {
		return error.message;
	}

	return typeof error === "string" ? error : "Unable to connect to the local chat hub.";
}

/**
 * A9 module-readiness gate (platform parity): eager-connects the shared chat hub on mount and reports a
 * readiness state the page uses to block render until the hub is live. The hub is otherwise lazily started on
 * first send; warming it here surfaces connection failures up front instead of on the first message.
 *
 * Once connected at least once the gate latches `ready` and never downgrades on transient reconnects — those
 * are handled in-band by withAutomaticReconnect, so re-blocking the whole chat mid-session would be jarring.
 */
export function useNodeChatConnectionReadiness(): NodeChatConnectionReadinessState {
	const [readiness, setReadiness] = useState<NodeChatConnectionReadiness>(() =>
		nodeChatConnection.status === "connected" ? "ready" : "connecting",
	);
	const [error, setError] = useState<string | undefined>(undefined);
	const hasConnectedRef = useRef(nodeChatConnection.status === "connected");
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
