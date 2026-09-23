import { Alert, Badge, Button, Divider, Group, Stack, Text } from "@mantine/core";
import { IconInfoCircle, IconRefresh } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { AppUpdateButton } from "@/features/app-update/components/AppUpdateButton";
import { AppUpdateChannelSelector } from "@/features/app-update/components/AppUpdateChannelSelector";
import { channelName } from "@/features/app-update/models/AppUpdateChannelCopy";
import { useAppUpdateStatus, useRefreshAppUpdateStatus } from "@/features/app-update/queries/useAppUpdate";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";

/** App-update section rendered inside the About dialog for managed portable desktop builds. */
export function AppUpdateSection() {
	const { t } = useTranslation();
	const { data: status } = useAppUpdateStatus();
	const refreshMutation = useRefreshAppUpdateStatus();

	if (!status?.isDesktop) {
		return null;
	}

	return (
		<Stack gap="sm">
			<Divider />
			<Group justify="space-between" align="center">
				<Text fw={500}>{t("pages.about.appUpdate.title")}</Text>
				{status.isConfigured ? (
					<Button
						variant="subtle"
						size="xs"
						leftSection={<IconRefresh size={14} />}
						loading={refreshMutation.isPending}
						onClick={() => refreshMutation.mutate()}
					>
						{t("pages.about.appUpdate.checkForUpdates")}
					</Button>
				) : null}
			</Group>

			<Group gap="xs">
				<Text size="sm" c="dimmed">
					{t("pages.about.version")}
				</Text>
				<Badge variant="light">{status.currentVersion}</Badge>
			</Group>

			{/* Above the check-status alerts: the channel is what those alerts are about. It hides itself when the
			    build has no update source, so the not-configured alert below still reads alone. */}
			<AppUpdateChannelSelector />

			{!status.isConfigured ? (
				<Alert icon={<IconInfoCircle size={16} />} color="gray">
					{t("pages.about.appUpdate.notConfigured")}
				</Alert>
			) : null}

			{status.isConfigured && status.checkStatus === "offline" ? (
				<Alert icon={<IconInfoCircle size={16} />} color="yellow">
					{t("pages.about.appUpdate.offline")}
				</Alert>
			) : null}

			{status.isConfigured && status.checkStatus === "failed" ? (
				<InlineErrorAlert icon={<IconInfoCircle size={16} />} message={t("pages.about.appUpdate.checkFailed")} />
			) : null}

			{status.isConfigured && status.checkStatus === "ready" ? (
				<Stack gap="xs">
					<AppUpdateButton />

					{status.availableVersion ? (
						<Group gap="xs">
							<Text size="sm" c="dimmed">
								{t("pages.about.appUpdate.availableVersion")}
							</Text>
							<Badge variant="dot" color="blue">
								{status.availableVersion}
							</Badge>
							{status.availableChannel ? (
								<Text size="sm" c="dimmed" data-testid="app-update-available-channel">
									{t("pages.about.appUpdate.availableFromChannel", { channel: channelName(t, status.availableChannel) })}
								</Text>
							) : null}
						</Group>
					) : null}
				</Stack>
			) : null}
		</Stack>
	);
}
