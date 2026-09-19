import { Stack, Textarea, TextInput } from "@mantine/core";
import { useTranslation } from "react-i18next";

/**
 * Server-side limits (`DevWorkflowRequestLimits`). Enforced here too so the operator is stopped by the input rather
 * than by a 400 after typing eight thousand characters.
 */
const TITLE_MAX = 200;
const REQUEST_MAX = 8000;

export interface WorkItemFieldValues {
	readonly title: string;
	readonly request: string;
}

export interface WorkItemFieldsProps {
	readonly values: WorkItemFieldValues;
	readonly onChange: (next: Partial<WorkItemFieldValues>) => void;
	/** Prefixes both fields' test ids, so create and edit stay separately addressable. */
	readonly testIdPrefix: string;
	/** Replaces the create flow's hint under Request. Edit says the change lands on the NEXT run. */
	readonly requestHint?: string;
	readonly titleError?: string;
	readonly requestError?: string;
}

/**
 * The two fields a work item's PATCH accepts — which are also the two the create dialog collects. Shared so the edit
 * dialog cannot drift from the create form's labels, limits or "a blank field is not a save" rule; everything else the
 * create dialog asks for (the template, the project) is fixed at creation and has no edit route behind it.
 */
export function WorkItemFields({ values, onChange, testIdPrefix, requestHint, titleError, requestError }: WorkItemFieldsProps) {
	const { t } = useTranslation();

	return (
		<Stack gap="md">
			<TextInput
				label={t("pages.devWorkflows.create.titleLabel", "Title")}
				value={values.title}
				maxLength={TITLE_MAX}
				required={true}
				error={titleError}
				onChange={(event) => onChange({ title: event.currentTarget.value })}
				data-testid={`${testIdPrefix}-title`}
			/>
			<Textarea
				label={t("pages.devWorkflows.create.requestLabel", "Request")}
				description={
					requestHint ??
					t(
						"pages.devWorkflows.create.requestHint",
						"What should this workflow deliver? The first node receives this text as its objective.",
					)
				}
				value={values.request}
				maxLength={REQUEST_MAX}
				required={true}
				autosize={true}
				minRows={4}
				maxRows={10}
				error={requestError}
				onChange={(event) => onChange({ request: event.currentTarget.value })}
				data-testid={`${testIdPrefix}-request`}
			/>
		</Stack>
	);
}
