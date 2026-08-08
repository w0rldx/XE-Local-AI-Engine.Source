import { Alert, Badge, Button, Divider, Group, Stack, Text } from "@mantine/core";
import { IconInfoCircle, IconRefresh } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { AppUpdateButton } from "@/features/app-update/components/AppUpdateButton";
import {
	useAppUpdateStatus,
	useRefreshAppUpdateStatus,
} from "@/features/app-update/queries/useAppUpdate";

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
				<Text size="sm" c="dimmed">{t("pages.about.version")}</Text>
				<Badge variant="light">{status.currentVersion}</Badge>
			</Group>

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
				<Alert icon={<IconInfoCircle size={16} />} color="red">
					{t("pages.about.appUpdate.checkFailed")}
				</Alert>
			) : null}

			{status.isConfigured && status.checkStatus === "ready" ? (
				<Stack gap="xs">
					<AppUpdateButton />

					{status.availableVersion ? (
						<Group gap="xs">
							<Text size="sm" c="dimmed">{t("pages.about.appUpdate.availableVersion")}</Text>
							<Badge variant="dot" color="blue">{status.availableVersion}</Badge>
						</Group>
					) : null}
				</Stack>
			) : null}
		</Stack>
	);
}
