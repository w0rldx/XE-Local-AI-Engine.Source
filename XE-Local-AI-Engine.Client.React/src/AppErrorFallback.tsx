import { Button, Center, Group, Paper, Stack, Text, Title } from "@mantine/core";
import { IconAlertCircle, IconBrandGithub, IconDownload } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { AppErrorFallbackProps } from "@/AppErrorFallback.types";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { buildIssueUrl } from "@/features/diagnostics/IssueUrl";
import { useNodeInfo } from "@/features/diagnostics/queries/useNodeInfo";
import { exportSupportBundle } from "@/features/diagnostics/SupportBundle";
import { useSnapshots } from "@/features/diagnostics/UseSnapshots";

// Rendered inside the Theme/Query/Confirm providers (App.tsx and the router's root error component), so queries and
// toasts work here. SupportBundle is imported statically on purpose: its only heavy dependency, fflate, is already in
// the boot graph through the rrweb recorder, and an `import()` here split the shared Mantine code into extra chunks
// that cost ~53 kB of bundle budget for no saving.
export function AppErrorFallback({ error, onRetry }: AppErrorFallbackProps) {
	const { t } = useTranslation();
	const errorMessage = apiErrorMessage(error, t("app.errorFallback.unknownError"));
	const { data: snapshots } = useSnapshots();
	const { data: nodeInfo } = useNodeInfo();
	const [exporting, setExporting] = useState(false);

	const newest = snapshots?.[0];
	const issueUrl = nodeInfo ? buildIssueUrl(nodeInfo, newest) : undefined;

	const handleExport = async (): Promise<void> => {
		if (!newest) {
			return;
		}
		setExporting(true);
		try {
			await exportSupportBundle(newest);
		} finally {
			setExporting(false);
		}
	};

	return (
		<Center mih="100dvh" p="md">
			<Paper withBorder={true} radius="md" p="xl" maw={560} w="100%">
				<Stack gap="md">
					<InlineErrorAlert
						icon={<IconAlertCircle size={18} />}
						title={t("app.errorFallback.alertTitle")}
						variant="light"
						message={t("app.errorFallback.alertMessage")}
					/>
					<Stack gap={4}>
						<Title order={1} size="h3">
							{t("app.errorFallback.title")}
						</Title>
						<Text c="dimmed">{t("app.errorFallback.description")}</Text>
					</Stack>
					<Text ff="monospace" size="sm">
						{errorMessage}
					</Text>
					<Group gap="sm">
						<Button onClick={onRetry}>{t("app.errorFallback.retry")}</Button>
						<Button
							variant="default"
							leftSection={<IconDownload size={16} />}
							disabled={!newest}
							loading={exporting}
							onClick={handleExport}
						>
							{t("app.errorFallback.exportDiagnostics")}
						</Button>
						{issueUrl && (
							<Button
								component="a"
								href={issueUrl}
								target="_blank"
								rel="noopener noreferrer"
								variant="default"
								leftSection={<IconBrandGithub size={16} />}
							>
								{t("app.errorFallback.openIssue")}
							</Button>
						)}
					</Group>
					{!newest && (
						<Text c="dimmed" size="sm">
							{t("app.errorFallback.exportUnavailable")}
						</Text>
					)}
				</Stack>
			</Paper>
		</Center>
	);
}
