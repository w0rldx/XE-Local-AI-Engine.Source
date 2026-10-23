import { Alert, Button, CloseButton, Group, Loader, Stack, Text } from "@mantine/core";
import { IconRefreshAlert } from "@tabler/icons-react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { type ReactNode, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getNodeInfoOptions, getNodeSettingsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { nodeAuthStatus, restartNode } from "@/core/api/generated/sdk.gen";
import { useRestartPoll } from "@/core/ui/hooks/useRestartPoll";
import { nodeSettingsFieldLabelText } from "@/features/node-settings/components/NodeSettingsFieldPresentation";

// App-shell banner for saved settings the running node has not applied yet: the node reports them as
// `pendingRestartFields` (gated fields whose stored value differs from the one it booted with). Persistent, because the
// change is invisible until a restart; dismissible for this session only (component state, mounted once in the shell).
// Inside the desktop app it offers the restart; a browser session cannot start the node again, so it only says so.

// The anonymous auth-status route (LocalApiRoutes.Auth.Status). Both the full host and the locked-vault host answer it
// with their processGeneration, so a node that restarts into its unlock page is still seen coming back.
const authStatusProbePath = "/api/local/v1/auth/status";
export function PendingRestartBanner() {
	const { t } = useTranslation();
	// The query the layout guard primed, kept live by mount and window-focus refetches: a change saved through MCP or
	// another browser has no push channel, so this is how it shows. It shares the key with the settings page.
	const { data: settings } = useQuery(getNodeSettingsOptions());
	const pendingFields = settings?.pendingRestartFields ?? [];
	// Only a node a desktop shell owns can be restarted: the shell starts it again. A standalone local-mode engine
	// answers the restart with 409.
	const { data: nodeInfo } = useQuery({ ...getNodeInfoOptions(), enabled: pendingFields.length > 0 });
	// Captures the running process's generation before asking for the restart: a different one is the new process.
	const restartMutation = useMutation({
		mutationFn: async () => {
			const { data } = await nodeAuthStatus();
			await restartNode();
			return data.processGeneration;
		},
	});
	const restartPoll = useRestartPoll();
	// Keyed by the pending set, not a boolean, so a newly pending field shows the banner again.
	const signature = pendingFields.toSorted().join(",");
	const [dismissedSignature, setDismissedSignature] = useState<string | null>(null);

	const canDismiss = restartPoll.status === "idle" || restartPoll.status === "timedOut";
	if (pendingFields.length === 0 || (dismissedSignature === signature && canDismiss)) {
		return null;
	}

	const handleRestart = async () => {
		let generation: string;
		try {
			generation = await restartMutation.mutateAsync();
		} catch {
			// The refusal is rendered from restartMutation.error.
			return;
		}
		// The reload lands on the unlock page when the restarted node's vault is locked.
		restartPoll.start(async () => (await nodeAuthStatus()).data.processGeneration !== generation, {
			probePath: authStatusProbePath,
		});
	};

	const isRestarting = restartPoll.status === "restarting" || restartPoll.status === "ready";

	let action: ReactNode;
	if (isRestarting) {
		action = (
			<Group gap="xs" wrap="nowrap" data-testid="pending-restart-banner-restarting">
				<Loader size="xs" />
				<Text size="sm">{t("pages.nodeSettings.pendingRestart.restarting", "Restarting…")}</Text>
			</Group>
		);
	} else if (nodeInfo?.isShellOwned === true) {
		action = (
			<Button
				size="xs"
				loading={restartMutation.isPending}
				onClick={() => {
					handleRestart().catch(() => undefined);
				}}
				data-testid="pending-restart-banner-restart"
			>
				{t("pages.nodeSettings.pendingRestart.restartNow", "Restart now")}
			</Button>
		);
	} else {
		action = (
			<Text size="sm" data-testid="pending-restart-banner-hint">
				{t("pages.nodeSettings.pendingRestart.browserHint", "Restart the node to apply.")}
			</Text>
		);
	}

	return (
		<Alert
			color="orange"
			variant="light"
			icon={<IconRefreshAlert size={18} />}
			radius={0}
			withCloseButton={false}
			title={t("pages.nodeSettings.pendingRestart.title", "{{count}} settings wait for a node restart", {
				count: pendingFields.length,
			})}
			data-testid="pending-restart-banner"
		>
			<Group justify="space-between" align="flex-start" wrap="nowrap" gap="sm">
				<Stack gap={2}>
					<Text size="sm" data-testid="pending-restart-banner-fields">
						{pendingFields.map((field) => nodeSettingsFieldLabelText(t, field)).join(", ")}
					</Text>
					{restartMutation.error ? (
						<Text size="xs" c="red" data-testid="pending-restart-banner-error">
							{apiErrorMessage(
								restartMutation.error,
								t("pages.nodeSettings.pendingRestart.restartError", "Could not restart the node."),
							)}
						</Text>
					) : null}
					{restartPoll.status === "timedOut" ? (
						<Text size="xs" c="red" data-testid="pending-restart-banner-timeout">
							{t(
								"pages.nodeSettings.pendingRestart.timedOut",
								"The node did not come back within a minute. Check that the app is running, then reload this page.",
							)}
						</Text>
					) : null}
				</Stack>
				<Group gap="xs" wrap="nowrap">
					{action}
					{isRestarting ? null : (
						<CloseButton
							aria-label={t("pages.nodeSettings.pendingRestart.dismiss", "Dismiss")}
							onClick={() => setDismissedSignature(signature)}
							data-testid="pending-restart-banner-dismiss"
						/>
					)}
				</Group>
			</Group>
		</Alert>
	);
}
