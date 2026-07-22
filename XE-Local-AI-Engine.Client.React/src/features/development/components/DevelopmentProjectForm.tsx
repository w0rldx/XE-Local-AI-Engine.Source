import { Button, Checkbox, Grid, NumberInput, Select, Stack, Textarea, TextInput } from "@mantine/core";
import { IconPlus } from "@tabler/icons-react";
import { type FormEvent, useState } from "react";
import { useTranslation } from "react-i18next";

export interface DevelopmentProjectFormValues {
	readonly repositoryRoot: string;
	readonly objective: string;
	readonly baseBranch: string;
	readonly taskTitle: string;
	readonly requirements: string;
	readonly acceptanceCriteriaJson: string;
	readonly egressPolicy: "LocalOnly" | "CloudScoped";
	readonly coderModelId: string;
	readonly reviewerModelId: string;
	readonly trustedRepositoryAcknowledged: boolean;
	readonly maxTokens?: number;
	readonly maxDurationSeconds?: number;
}

interface DevelopmentProjectFormProps {
	readonly isSubmitting: boolean;
	readonly error?: string;
	readonly onSubmit: (values: DevelopmentProjectFormValues) => void;
}

const initialValues: DevelopmentProjectFormValues = {
	repositoryRoot: "",
	objective: "",
	baseBranch: "main",
	taskTitle: "",
	requirements: "",
	acceptanceCriteriaJson: "[]",
	egressPolicy: "LocalOnly",
	coderModelId: "",
	reviewerModelId: "",
	trustedRepositoryAcknowledged: false,
};

export function DevelopmentProjectForm({ isSubmitting, error, onSubmit }: DevelopmentProjectFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState(initialValues);

	const submit = (event: FormEvent<HTMLFormElement>): void => {
		event.preventDefault();
		onSubmit(values);
	};

	return (
		<form onSubmit={submit} data-testid="development-project-form">
			<Stack gap="md">
				<Grid>
					<Grid.Col span={{ base: 12, md: 8 }}>
						<TextInput
							label={t("pages.development.form.repositoryRoot", "Repository root")}
							description={t(
								"pages.development.form.repositoryDescription",
								"The repository must be local and explicitly trusted. The worktree isolates changes; it is not an OS sandbox.",
							)}
							value={values.repositoryRoot}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setValues((current) => ({ ...current, repositoryRoot: value }));
							}}
							required={true}
							data-testid="development-repository-root"
						/>
					</Grid.Col>
					<Grid.Col span={{ base: 12, md: 4 }}>
						<TextInput
							label={t("pages.development.form.baseBranch", "Base branch")}
							value={values.baseBranch}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setValues((current) => ({ ...current, baseBranch: value }));
							}}
							required={true}
						/>
					</Grid.Col>
				</Grid>
				<Textarea
					label={t("pages.development.form.objective", "Project objective")}
					value={values.objective}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setValues((current) => ({ ...current, objective: value }));
					}}
					minRows={2}
					required={true}
				/>
				<TextInput
					label={t("pages.development.form.taskTitle", "Initial task title")}
					value={values.taskTitle}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setValues((current) => ({ ...current, taskTitle: value }));
					}}
					required={true}
				/>
				<Textarea
					label={t("pages.development.form.requirements", "Requirements")}
					value={values.requirements}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setValues((current) => ({ ...current, requirements: value }));
					}}
					minRows={4}
					required={true}
				/>
				<Textarea
					label={t("pages.development.form.acceptanceCriteria", "Acceptance criteria (JSON)")}
					value={values.acceptanceCriteriaJson}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setValues((current) => ({ ...current, acceptanceCriteriaJson: value }));
					}}
					minRows={3}
					required={true}
				/>
				<Grid>
					<Grid.Col span={{ base: 12, md: 4 }}>
						<Select
							label={t("pages.development.form.egressPolicy", "Cloud policy")}
							value={values.egressPolicy}
							data={[
								{ value: "LocalOnly", label: t("pages.development.policy.localOnly", "Local only") },
								{ value: "CloudScoped", label: t("pages.development.policy.cloudScoped", "Cloud scoped") },
							]}
							onChange={(value) =>
								setValues((current) => ({ ...current, egressPolicy: value === "CloudScoped" ? "CloudScoped" : "LocalOnly" }))
							}
						/>
					</Grid.Col>
					<Grid.Col span={{ base: 12, md: 4 }}>
						<TextInput
							label={t("pages.development.form.coderModel", "Coder model ID")}
							value={values.coderModelId}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setValues((current) => ({ ...current, coderModelId: value }));
							}}
							required={true}
						/>
					</Grid.Col>
					<Grid.Col span={{ base: 12, md: 4 }}>
						<TextInput
							label={t("pages.development.form.reviewerModel", "Reviewer model ID")}
							value={values.reviewerModelId}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setValues((current) => ({ ...current, reviewerModelId: value }));
							}}
							required={true}
						/>
					</Grid.Col>
				</Grid>
				<Grid>
					<Grid.Col span={{ base: 12, sm: 6 }}>
						<NumberInput
							label={t("pages.development.form.maxTokens", "Maximum tokens (optional)")}
							min={1}
							value={values.maxTokens}
							onChange={(value) =>
								setValues((current) => ({ ...current, maxTokens: typeof value === "number" ? value : undefined }))
							}
						/>
					</Grid.Col>
					<Grid.Col span={{ base: 12, sm: 6 }}>
						<NumberInput
							label={t("pages.development.form.maxDuration", "Maximum duration in seconds (optional)")}
							min={1}
							value={values.maxDurationSeconds}
							onChange={(value) =>
								setValues((current) => ({ ...current, maxDurationSeconds: typeof value === "number" ? value : undefined }))
							}
						/>
					</Grid.Col>
				</Grid>
				<Checkbox
					checked={values.trustedRepositoryAcknowledged}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						setValues((current) => ({ ...current, trustedRepositoryAcknowledged: checked }));
					}}
					label={t(
						"pages.development.form.trustAcknowledgement",
						"I trust this repository to run the fixed Development command catalog with the configured process sandbox.",
					)}
					data-testid="development-trust-acknowledgement"
				/>
				{error ? <div role="alert">{error}</div> : null}
				<Button
					type="submit"
					leftSection={<IconPlus size={16} />}
					loading={isSubmitting}
					disabled={!values.trustedRepositoryAcknowledged}
					data-testid="development-create-project"
				>
					{t("pages.development.form.create", "Create Development project")}
				</Button>
			</Stack>
		</form>
	);
}
