import { Button, Group, Modal, Progress, Stack, Text, TextInput } from "@mantine/core";
import { IconCloudDownload } from "@tabler/icons-react";

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

// Pull-a-new-model dialog. Background-click close is always disabled and, while a pull is in flight, Escape and the
// close button are removed too — a model download can take minutes, and dismissing the dialog mid-pull would leave
// the user wondering why "nothing happened". The pull itself runs in the parent mutation; this dialog only collects
// the model name and reflects progress.
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
	const pullNameToSubmit = pullModelName.trim();

	return (
		<Modal
			opened={opened}
			onClose={onClose}
			title="Pull new model"
			closeOnClickOutside={false}
			closeOnEscape={!isPulling}
			withCloseButton={!isPulling}
		>
			<Stack gap="md">
				<TextInput
					data-testid="pull-model-name-input"
					label="Model name to pull"
					placeholder="orca-mini:latest"
					value={pullModelName}
					onChange={(event) => onPullModelNameChange(event.currentTarget.value)}
					disabled={isPulling}
				/>
				<Group>
					<Button
						data-testid="download-model-button"
						leftSection={<IconCloudDownload size={16} />}
						disabled={!pullNameToSubmit || isActionPending}
						loading={isPulling}
						onClick={onSubmit}
					>
						Download model
					</Button>
				</Group>
				{progress !== undefined ? <Progress value={progress} aria-label="Pull progress" /> : null}
				{isPulling ? (
					<Text size="xs" c="dimmed">
						Download in progress — keep this dialog open until it finishes.
					</Text>
				) : null}
			</Stack>
		</Modal>
	);
}
