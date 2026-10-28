import { Alert, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientEndpointsDevelopmentV1SandboxIsolationSummaryResponse as SandboxIsolation } from "@/core/api/generated/types.gen";
import { isDevelopmentContainerProvider } from "@/features/development/models/DevelopmentModels";

interface DevelopmentFilesystemBoundaryNoticeProps {
	readonly roles: readonly SandboxIsolation[] | undefined;
}

/**
 * The persistent warning for a Development run with no filesystem boundary on this host, read from the `development`
 * isolation row the capability endpoint already reports.
 *
 * Shown only where the role asks for the boundary and runs without it: the process provider without bubblewrap under the
 * `low` profile. A container has the boundary by construction, and under `high` the role is refused rather than run, which
 * the isolation panel already says, so neither gets this sentence.
 */
export function DevelopmentFilesystemBoundaryNotice({ roles }: DevelopmentFilesystemBoundaryNoticeProps) {
	const { t } = useTranslation();
	const row = roles?.find((role) => role.role === "development");
	if (!row || row.filesystemIsolation || row.filesystemIsolationRequired || isDevelopmentContainerProvider(row.provider)) {
		return null;
	}

	return (
		<Alert
			color="yellow"
			icon={<IconAlertTriangle size={16} />}
			title={t("pages.development.isolation.noBoundaryTitle", "No filesystem boundary on this host")}
			data-testid="development-no-filesystem-boundary"
		>
			<Stack gap={4}>
				<Text size="sm">
					{t(
						"pages.development.isolation.noBoundary",
						"The coder can write outside its workspace: its commands run as the account that runs the engine, with that account's access to your files.",
					)}
				</Text>
				{row.filesystemIsolationUnavailableReason ? (
					<Text size="xs" c="dimmed" data-testid="development-no-filesystem-boundary-reason">
						{row.filesystemIsolationUnavailableReason}
					</Text>
				) : null}
			</Stack>
		</Alert>
	);
}
