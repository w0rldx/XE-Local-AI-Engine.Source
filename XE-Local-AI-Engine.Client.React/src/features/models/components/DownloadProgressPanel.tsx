import { Button, Card, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconCloudDownload, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

interface DownloadProgressPanelProps {
	// Model names of in-flight GGUF downloads to surface. Derived by the page from the started-this-session set; there
	// is no byte-level progress from the backend, so each entry shows an indeterminate "downloading" state + cancel.
	inFlight: readonly string[];
	onCancel: (modelName: string) => void;
	cancellingModelName: string | null;
}

// In-flight GGUF download panel. The backend exposes no byte-level progress granularity, so this surfaces each
// download as an indeterminate "downloading" row with a cancel action (cancelGgufDownload) — it never fabricates a
// percentage. Rendered only when there is at least one in-flight download.
export function DownloadProgressPanel({ inFlight, onCancel, cancellingModelName }: DownloadProgressPanelProps) {
	const { t } = useTranslation();

	if (inFlight.length === 0) {
		return null;
	}

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="model-fit-download-card">
			<Stack gap="md">
				<Group gap="xs" align="center">
					<IconCloudDownload size={20} />
					<Title order={4}>{t("pages.models.gguf.download.title", "Downloads in progress")}</Title>
				</Group>

				<Stack gap="sm">
					{inFlight.map((modelName) => (
						<Group key={modelName} justify="space-between" align="center" data-testid={`model-fit-download-row-${modelName}`}>
							<Group gap="sm" align="center">
								<Loader size="xs" />
								<Stack gap={0}>
									<Text size="sm" fw={500}>
										{modelName}
									</Text>
									<Text size="xs" c="dimmed">
										{t("pages.models.gguf.download.inProgress", "Downloading…")}
									</Text>
								</Stack>
							</Group>
							<Button
								size="xs"
								variant="light"
								color="red"
								leftSection={<IconX size={14} />}
								loading={cancellingModelName === modelName}
								disabled={cancellingModelName === modelName}
								onClick={() => onCancel(modelName)}
								data-testid={`model-fit-download-cancel-${modelName}`}
							>
								{t("pages.models.gguf.download.cancel", "Cancel")}
							</Button>
						</Group>
					))}
				</Stack>
			</Stack>
		</Card>
	);
}
