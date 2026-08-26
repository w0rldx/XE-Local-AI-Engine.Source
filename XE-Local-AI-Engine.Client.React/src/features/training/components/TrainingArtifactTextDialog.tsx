import { Button, Stack, Text, TextInput } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";

type DialogKind = "override" | "discard" | "promote";

interface TrainingArtifactTextDialogProps {
	readonly kind: DialogKind;
	readonly opened: boolean;
	readonly value: string;
	readonly pending: boolean;
	readonly onChange: (value: string) => void;
	readonly onClose: () => void;
	readonly onConfirm: () => void;
}

export function TrainingArtifactTextDialog({
	kind,
	opened,
	value,
	pending,
	onChange,
	onClose,
	onConfirm,
}: TrainingArtifactTextDialogProps) {
	const { t } = useTranslation();
	const promote = kind === "promote";
	const discard = kind === "discard";
	const title = promote
		? t("training.artifacts.promoteTitle", "Register as a local model")
		: discard
			? t("training.artifacts.discardTitle", "Discard staged artifact")
			: t("training.artifacts.overrideTitle", "Override failed quality decision");
	const label = promote
		? t("training.artifacts.modelNameLabel", "Model name")
		: discard
			? t("training.artifacts.discardReason", "Discard reason")
			: t("training.artifacts.overrideReason", "Override reason");
	const action = promote
		? t("training.artifacts.promote", "Register as model")
		: discard
			? t("training.artifacts.confirmDiscard", "Confirm discard")
			: t("training.artifacts.recordOverride", "Record override");

	return (
		<DialogShell onClose={onClose} opened={opened} title={title}>
			<Stack gap="sm">
				<TextInput
					label={label}
					onChange={(event) => onChange(event.currentTarget.value)}
					placeholder={promote ? t("training.artifacts.modelNamePlaceholder", "my-tuned-model") : undefined}
					value={value}
				/>
				{promote ? (
					<Text c="dimmed" size="xs">
						{t(
							"training.artifacts.promoteNote",
							"The quantization is appended to the name automatically, and the model records the checkpoint and dataset it came from.",
						)}
					</Text>
				) : null}
				<Button color={discard ? "red" : undefined} disabled={value.trim().length === 0} loading={pending} onClick={onConfirm}>
					{action}
				</Button>
			</Stack>
		</DialogShell>
	);
}
