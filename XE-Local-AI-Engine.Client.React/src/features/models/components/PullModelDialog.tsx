import { Alert, Anchor, Button, Progress, Stack, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconCloudDownload, IconInfoCircle } from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";

interface PullModelDialogProps {
	opened: boolean;
	onClose: () => void;
	pullModelName: string;
	onPullModelNameChange: (value: string) => void;
	onSubmit: () => void;
	isPulling: boolean;
	isActionPending: boolean;
	progress: number | undefined;
}

// Pull-a-new-model dialog. Background-click close is always disabled and, while a pull is in flight,
// the title-bar close button and Escape are removed entirely — a model download can take minutes,
// and dismissing mid-pull would leave the user wondering why "nothing happened". The pull itself runs
// in the parent's shared useModelPull hook; this dialog collects the model name, surfaces compatibility
// guidance + an unvetted-weights warning, and reflects the hook's live progress.
export function PullModelDialog({
	opened,
	onClose,
	pullModelName,
	onPullModelNameChange,
	onSubmit,
	isPulling,
	isActionPending,
	progress,
}: PullModelDialogProps) {
	const { t } = useTranslation();
	const pullNameToSubmit = pullModelName.trim();

	return (
		<DialogShell
			opened={opened}
			onClose={onClose}
			title={t("pages.models.pull.title", "Pull new model")}
			closeOnClickOutside={false}
			showCloseButton={!isPulling}
			closeOnEscape={!isPulling}
			footer={
				<Button
					data-testid="download-model-button"
					leftSection={<IconCloudDownload size={16} />}
					disabled={!pullNameToSubmit || isActionPending}
					loading={isPulling}
					onClick={onSubmit}
				>
					{t("pages.models.pull.downloadButton", "Download model")}
				</Button>
			}
		>
			<Stack gap="md">
				{/* Where to find a compatible tag — a manual pull must reference an Ollama-library-compatible model. */}
				<Alert color="blue" icon={<IconInfoCircle size={16} />} data-testid="pull-model-guidance">
					<Text size="sm">
						{t(
							"pages.models.pull.guidance",
							"Enter an Ollama-compatible model tag. Browse available models in the Ollama library.",
						)}
					</Text>
					<Anchor href="https://ollama.com/library" target="_blank" rel="noreferrer noopener" size="sm">
						{t("pages.models.pull.libraryLink", "Open the Ollama library (ollama.com/library)")}
					</Anchor>
				</Alert>

				{/* Deliberate safety surface (operator decision): a manual pull is unvetted and points back to the
				    reviewed recommendations for this node. */}
				<Alert color="yellow" icon={<IconAlertTriangle size={16} />} data-testid="pull-model-warning">
					<Text size="sm">
						{t(
							"pages.models.pull.warning",
							"A manual pull downloads arbitrary, unvetted model weights and can be very large. Prefer the recommended models for this node.",
						)}
					</Text>
					<Anchor
						component={Link}
						to={nodeRoutePaths.modelRecommendations}
						size="sm"
						data-testid="pull-model-recommendations-link"
					>
						{t("pages.models.pull.recommendationsLink", "See recommended models for this node")}
					</Anchor>
				</Alert>

				<TextInput
					data-testid="pull-model-name-input"
					label={t("pages.models.pull.inputLabel", "Model name to pull")}
					placeholder="orca-mini:latest"
					value={pullModelName}
					onChange={(event) => onPullModelNameChange(event.currentTarget.value)}
					disabled={isPulling}
				/>
				{progress !== undefined ? <Progress value={progress} aria-label="Pull progress" /> : null}
				{isPulling ? (
					<Text size="xs" c="dimmed">
						{t("pages.models.pull.inProgress", "Download in progress — keep this dialog open until it finishes.")}
					</Text>
				) : null}
			</Stack>
		</DialogShell>
	);
}
