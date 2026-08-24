import { Alert, Button, Group, Stack, TextInput, Textarea } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import type { WorkSessionStatus } from "@/features/workSessions/models/WorkSessionModels";

export interface EditWorkSessionDialogProps {
	readonly opened: boolean;
	readonly status: WorkSessionStatus;
	readonly initialTitle: string;
	readonly initialObjective: string;
	readonly isSubmitting: boolean;
	readonly errorMessage?: string;
	readonly onClose: () => void;
	readonly onSubmit: (values: { title: string; objective: string }) => void;
}

/**
 * Title + objective. The kind and the agent are not editable here: both are pinned for the session's life, and a
 * mid-run agent swap would change what the next step can even do.
 *
 * The server allows a title edit in any state but the OBJECTIVE only in `Draft | Paused | Interrupted` (a running
 * step has already been prompted with the old one). The field is disabled rather than allowed-then-409'd so the
 * operator learns the rule before typing.
 */
export function EditWorkSessionDialog({
	opened,
	status,
	initialTitle,
	initialObjective,
	isSubmitting,
	errorMessage,
	onClose,
	onSubmit,
}: EditWorkSessionDialogProps) {
	const { t } = useTranslation();
	const [title, setTitle] = useState(initialTitle);
	const [objective, setObjective] = useState(initialObjective);

	const objectiveEditable = status === "Draft" || status === "Paused" || status === "Interrupted";
	const trimmedTitle = title.trim();
	const trimmedObjective = objective.trim();
	const isDirty = trimmedTitle !== initialTitle.trim() || trimmedObjective !== initialObjective.trim();
	const canSubmit = trimmedTitle.length > 0 && trimmedObjective.length > 0 && isDirty && !isSubmitting;

	const close = (): void => {
		setTitle(initialTitle);
		setObjective(initialObjective);
		onClose();
	};

	return (
		<DialogShell
			opened={opened}
			onClose={close}
			title={t("pages.workSessions.edit.title", "Edit work session")}
			data-testid="edit-work-session-dialog"
			confirmCloseWhen={isDirty}
			footer={
				<Group justify="flex-end">
					<Button variant="subtle" onClick={close} data-testid="edit-work-session-cancel">
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						onClick={() => onSubmit({ title: trimmedTitle, objective: trimmedObjective })}
						disabled={!canSubmit}
						loading={isSubmitting}
						data-testid="edit-work-session-submit"
					>
						{t("pages.workSessions.edit.submit", "Save")}
					</Button>
				</Group>
			}
		>
			<Stack gap="md">
				{errorMessage ? (
					<Alert color="red" variant="light" data-testid="edit-work-session-error">
						{errorMessage}
					</Alert>
				) : null}
				<TextInput
					label={t("pages.workSessions.create.titleLabel", "Title")}
					value={title}
					maxLength={200}
					required={true}
					onChange={(event) => setTitle(event.currentTarget.value)}
					data-testid="edit-work-session-title"
				/>
				<Textarea
					label={t("pages.workSessions.create.objectiveLabel", "Objective")}
					description={
						objectiveEditable
							? undefined
							: t("pages.workSessions.edit.objectiveLocked", "The objective can only change while the session is paused or not yet started.")
					}
					value={objective}
					maxLength={8000}
					required={true}
					disabled={!objectiveEditable}
					autosize={true}
					minRows={4}
					maxRows={10}
					onChange={(event) => setObjective(event.currentTarget.value)}
					data-testid="edit-work-session-objective"
				/>
			</Stack>
		</DialogShell>
	);
}
