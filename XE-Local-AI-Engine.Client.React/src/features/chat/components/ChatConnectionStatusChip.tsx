import { Group, Loader, Paper, Text } from "@mantine/core";
import { IconPlugConnectedX } from "@tabler/icons-react";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { type NodeChatConnectionStatus, nodeChatConnection } from "@/features/chat/api/NodeChatConnection";

/**
 * UX-08: a passive, global status chip for the shared chat hub. It sits in a fixed corner of the authed shell and
 * appears only when the live connection is mid-session unhealthy:
 *   – "reconnecting" → always shown ("Reconnecting…"), the transient SignalR auto-reconnect window;
 *   – "disconnected" → shown as "Offline" ONLY after the hub has connected at least once, so it reflects a real
 *     mid-session drop rather than the pre-first-connect state on initial load.
 * It auto-hides on "connected"/"connecting". Scoped to the chat hub only — not a multi-hub aggregator.
 */
export function ChatConnectionStatusChip() {
	const { t } = useTranslation();
	const [status, setStatus] = useState<NodeChatConnectionStatus>(() => nodeChatConnection.status);
	// Latches once the hub has ever connected so a "disconnected" state before the first successful connect (normal on
	// initial load) does not flash an "Offline" chip — only a genuine mid-session drop does.
	const hasConnectedRef = useRef(nodeChatConnection.status === "connected");

	useEffect(() => {
		// Re-sync in case the status changed between initial render and effect run.
		const current = nodeChatConnection.status;
		if (current === "connected") {
			hasConnectedRef.current = true;
		}
		setStatus(current);

		const unsubscribe = nodeChatConnection.subscribe({
			onStatusChange: (next) => {
				if (next === "connected") {
					hasConnectedRef.current = true;
				}
				setStatus(next);
			},
		});
		return unsubscribe;
	}, []);

	const showReconnecting = status === "reconnecting";
	const showOffline = status === "disconnected" && hasConnectedRef.current;
	if (!showReconnecting && !showOffline) {
		return null;
	}

	return (
		<Paper
			withBorder={true}
			radius="xl"
			px="md"
			py={6}
			shadow="sm"
			role="status"
			aria-live="polite"
			data-testid="chat-connection-status-chip"
			data-status={status}
			style={{ position: "fixed", bottom: 16, right: 16, zIndex: 400 }}
		>
			<Group gap={8} align="center" wrap="nowrap">
				{showReconnecting ? (
					<>
						<Loader size="xs" color="yellow" />
						<Text size="xs" fw={500} c="yellow.8">
							{t("components.chatConnectionStatus.reconnecting", "Reconnecting…")}
						</Text>
					</>
				) : (
					<>
						<IconPlugConnectedX size={14} color="var(--mantine-color-red-6)" />
						<Text size="xs" fw={500} c="red.7">
							{t("components.chatConnectionStatus.offline", "Offline")}
						</Text>
					</>
				)}
			</Group>
		</Paper>
	);
}
