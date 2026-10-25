import { Badge, Button, Card, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconDevices } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import {
	useInvalidateNodeAuthSessions,
	useNodeAuthSessions,
	useRevokeAllNodeAuthSessions,
	useRevokeNodeAuthSession,
} from "@/features/node-settings/queries/useNodeAuthSessions";

// Every sign-in (browser, desktop window, terminal) is a session. Ending one rejects its tokens at once; ending this
// browser's own session (its row, or sign out everywhere, which includes it) is a sign-out, handled like logout: clear
// the auth store and go to /login.
export function NodeSessionsCard() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const navigate = useNavigate();
	const clearAuth = useNodeAuthStore((state) => state.actions.clear);
	const sessions = useNodeAuthSessions();
	const invalidate = useInvalidateNodeAuthSessions();
	const revoke = useRevokeNodeAuthSession();
	const revokeAll = useRevokeAllNodeAuthSessions();
	const isBusy = revoke.isPending || revokeAll.isPending;

	const signedOut = async (): Promise<void> => {
		clearAuth();
		await navigate({ to: "/login" });
	};

	const failed = (error: unknown): void => {
		toast.error(apiErrorMessage(error, t("pages.nodeSettings.sessions.error", "Could not end the session.")));
	};

	const handleRevoke = (sessionId: string, current: boolean): void => {
		revoke.mutate({ path: { sessionId } }, { onSuccess: () => (current ? signedOut() : invalidate()), onError: failed });
	};

	const handleRevokeAll = async (): Promise<void> => {
		const confirmed = await confirm({
			title: t("pages.nodeSettings.sessions.revokeAllConfirm.title", "Sign out everywhere?"),
			description: t(
				"pages.nodeSettings.sessions.revokeAllConfirm.description",
				"Every session ends at once, this one included. You will be sent to the sign-in page.",
			),
			confirmationText: t("pages.nodeSettings.sessions.revokeAllConfirm.confirm", "Sign out everywhere"),
			cancellationText: t("common.cancel", "Cancel"),
		});
		if (confirmed) {
			revokeAll.mutate({}, { onSuccess: () => signedOut(), onError: failed });
		}
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="node-sessions-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Group gap="xs" align="center">
						<IconDevices size={20} />
						<Title order={2} size="h4">
							{t("pages.nodeSettings.sessions.title", "Sign-in sessions")}
						</Title>
					</Group>
					<Button
						variant="light"
						color="red"
						size="xs"
						disabled={isBusy}
						onClick={handleRevokeAll}
						data-testid="node-sessions-revoke-all"
					>
						{t("pages.nodeSettings.sessions.revokeAll", "Sign out everywhere")}
					</Button>
				</Group>

				<Text size="sm" c="dimmed">
					{t(
						"pages.nodeSettings.sessions.description",
						"Each place you signed in to this node. Signing a session out ends it at once.",
					)}
				</Text>

				{sessions.isLoading ? <Loader size="sm" /> : null}
				{sessions.isError ? (
					<Text size="sm" c="red">
						{t("pages.nodeSettings.sessions.loadError", "Could not load the sessions.")}
					</Text>
				) : null}

				{sessions.data?.map((session) => (
					<Group key={session.id} justify="space-between" align="center" wrap="nowrap" data-testid={`node-session-${session.id}`}>
						<Stack gap={2} style={{ minWidth: 0 }}>
							<Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
								<Text size="sm" fw={500} truncate="end" title={session.clientLabel ?? undefined}>
									{session.clientLabel ?? t("pages.nodeSettings.sessions.unknownClient", "Unknown client")}
								</Text>
								{session.current ? (
									<Badge size="sm" variant="light">
										{t("pages.nodeSettings.sessions.thisDevice", "This device")}
									</Badge>
								) : null}
							</Group>
							<Text size="xs" c="dimmed">
								{t("pages.nodeSettings.sessions.times", "Signed in {{created}} · last used {{lastUsed}}", {
									created: formatTimestamp(session.createdAtUtc),
									lastUsed: formatTimestamp(session.lastUsedAtUtc),
								})}
							</Text>
						</Stack>
						<Button
							variant="subtle"
							color="red"
							size="xs"
							disabled={isBusy}
							onClick={() => handleRevoke(session.id, session.current)}
							data-testid={`node-session-revoke-${session.id}`}
						>
							{t("pages.nodeSettings.sessions.revoke", "Sign out")}
						</Button>
					</Group>
				))}
			</Stack>
		</Card>
	);
}
