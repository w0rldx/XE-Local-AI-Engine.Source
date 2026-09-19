import { Button, Group, Stack } from "@mantine/core";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { type WorkItemFieldValues, WorkItemFields } from "@/features/devWorkflows/components/WorkItemFields";
import type { DevWorkflowWorkItemResponse } from "@/features/devWorkflows/models/DevWorkflowModels";

/** The PATCH body: an ABSENT member means "leave it alone", which is the endpoint's own reading of a null. */
export interface EditWorkItemValues {
	readonly title?: string;
	readonly request?: string;
}

export interface EditWorkItemDialogProps {
	readonly opened: boolean;
	readonly workItem: DevWorkflowWorkItemResponse;
	readonly isSubmitting: boolean;
	/** The thrown failure itself, not a message: a 400 names the field it refused, and this is where that lands. */
	readonly error?: unknown;
	readonly onClose: () => void;
	readonly onSubmit: (values: EditWorkItemValues) => void;
}

/**
 * Per-field refusals off a FastEndpoints 400 (`errors: [{ name, reason }]`). The name is the DTO property, whose
 * casing is the host's global serializer setting rather than anything this client can pin, so it is matched
 * case-insensitively. A refusal naming no field falls through to the alert above the form.
 */
function fieldErrors(error: unknown): { title?: string; request?: string } {
	if (!(error instanceof ApiError) || error.statusCode !== 400) {
		return {};
	}
	const errors =
		(error.apiProblemDetails as { errors?: readonly { name?: string; reason?: string }[] } | undefined)?.errors ?? [];
	const reasonFor = (field: string): string | undefined =>
		errors.find((entry) => entry.name?.toLowerCase() === field && (entry.reason?.trim().length ?? 0) > 0)?.reason;
	return { title: reasonFor("title"), request: reasonFor("request") };
}

/**
 * Rename a work item, or restate what it asks for. Title and request are the ONLY editable fields — the template is
 * chosen per run and the development project is fixed at creation, so neither has an update route behind it.
 *
 * There is no state in which the server refuses this: the runtime's only write to a work item is its STATUS, which
 * this PATCH never touches, so the two cannot collide and the action stays available while a run is live.
 */
export function EditWorkItemDialog({ opened, workItem, isSubmitting, error, onClose, onSubmit }: EditWorkItemDialogProps) {
	const { t } = useTranslation();
	const initial: WorkItemFieldValues = { title: workItem.title ?? "", request: workItem.request ?? "" };
	const [values, setValues] = useState<WorkItemFieldValues>(initial);

	// Seeded on identity and version, not on the row object: a poll that re-fetches the same work item must not throw
	// away what the operator is typing, while reopening the dialog — or a save that bumped the version — reseeds it.
	const seedKey = `${workItem.id ?? ""}:${workItem.version ?? 0}:${opened}`;
	// biome-ignore lint/correctness/useExhaustiveDependencies: seeding is keyed on identity, not on the row object.
	useEffect(() => {
		setValues({ title: workItem.title ?? "", request: workItem.request ?? "" });
	}, [seedKey]);

	const title = values.title.trim();
	const request = values.request.trim();
	const titleChanged = title !== initial.title.trim();
	const requestChanged = request !== initial.request.trim();
	const isDirty = titleChanged || requestChanged;
	// Mirrors `UpdateDevWorkflowWorkItemRequestValidator`: a PRESENT member must not be blank. Sending nothing at all
	// would be accepted by the server and change nothing, which is a request worth not making.
	const canSubmit = title.length > 0 && request.length > 0 && isDirty && !isSubmitting;
	const refusals = fieldErrors(error);

	return (
		<DialogShell
			opened={opened}
			onClose={onClose}
			title={t("pages.devWorkflows.edit.title", "Edit work item")}
			data-testid="edit-dev-workflow-work-item-dialog"
			// Unsaved edits: a stray overlay click must not discard them.
			confirmCloseWhen={isDirty}
			footer={
				<Group justify="flex-end">
					<Button variant="subtle" onClick={onClose} data-testid="edit-dev-workflow-work-item-cancel">
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						// Only what CHANGED: an absent member is what tells the endpoint to leave the other one alone.
						onClick={() => onSubmit({ ...(titleChanged ? { title } : {}), ...(requestChanged ? { request } : {}) })}
						disabled={!canSubmit}
						loading={isSubmitting}
						data-testid="edit-dev-workflow-work-item-submit"
					>
						{t("common.save", "Save")}
					</Button>
				</Group>
			}
		>
			<Stack gap="md">
				{error && !refusals.title && !refusals.request ? (
					<InlineErrorAlert
						message={apiErrorMessage(error, t("pages.devWorkflows.edit.failed", "Could not save this work item."))}
						variant="light"
						data-testid="edit-dev-workflow-work-item-error"
					/>
				) : null}
				<WorkItemFields
					values={values}
					onChange={(next) => setValues((current) => ({ ...current, ...next }))}
					testIdPrefix="edit-dev-workflow-work-item"
					requestHint={t(
						"pages.devWorkflows.edit.requestHint",
						"A run that has already started keeps the text it was given; this is what the next run receives.",
					)}
					titleError={refusals.title}
					requestError={refusals.request}
				/>
			</Stack>
		</DialogShell>
	);
}
