import { Alert, Button, Checkbox, Grid, Loader, NumberInput, Select, Stack, Text, Textarea, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconFolderPlus, IconPlus, IconTemplate } from "@tabler/icons-react";
import type { TFunction } from "i18next";
import type { Dispatch, FormEvent, SetStateAction } from "react";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import type { DevelopmentProfileDetection } from "@/features/development/models/DevelopmentModels";
import type {
	DevelopmentFormSelectOption,
	DevelopmentProjectFormValues,
} from "@/features/development/models/DevelopmentProjectFormModels";

export interface ProjectDraftSectionProps {
	readonly containerProvider: boolean;
	readonly values: DevelopmentProjectFormValues;
	readonly setValues: Dispatch<SetStateAction<DevelopmentProjectFormValues>>;
	readonly repositoryOptions: readonly DevelopmentFormSelectOption[];
	readonly repositoriesLoading: boolean;
	readonly repositoriesError?: string;
	readonly onRepositoryChange?: (selectedFolderId: string) => void;
	readonly detection?: DevelopmentProfileDetection | null;
	readonly detectionLoading: boolean;
	readonly detectionError?: string;
	readonly candidates: readonly string[];
	readonly chosenBuildTarget: string | null;
	readonly chosenProfileId: string | null;
	readonly whitespaceOnly: boolean;
	readonly profileConfirmed: boolean;
	readonly setProfileConfirmed: Dispatch<SetStateAction<boolean>>;
	readonly canCreate: boolean;
	readonly isSubmitting: boolean;
	readonly error?: string;
	readonly submit: (event: FormEvent<HTMLFormElement>) => void;
	readonly setRegistrationOpened: Dispatch<SetStateAction<boolean>>;
	readonly setRegistrationAttemptError: Dispatch<SetStateAction<string | undefined>>;
	readonly setTemplateOpened: Dispatch<SetStateAction<boolean>>;
	readonly setTemplateCreationError: Dispatch<SetStateAction<string | undefined>>;
	readonly setTemplateRegistryError: Dispatch<SetStateAction<string | undefined>>;
}

export function ProjectDraftSection({
	t,
	project: props,
}: {
	readonly t: TFunction;
	readonly project: ProjectDraftSectionProps;
}) {
	const {
		containerProvider,
		values,
		setValues,
		repositoryOptions,
		repositoriesLoading,
		repositoriesError,
		onRepositoryChange,
		detection,
		detectionLoading,
		detectionError,
		candidates,
		chosenBuildTarget,
		chosenProfileId,
		whitespaceOnly,
		profileConfirmed,
		setProfileConfirmed,
		canCreate,
		isSubmitting,
		error,
		submit,
		setRegistrationOpened,
		setRegistrationAttemptError,
		setTemplateOpened,
		setTemplateCreationError,
		setTemplateRegistryError,
	} = props;
	return (
		<form onSubmit={submit} data-testid="development-project-form">
			<Stack gap="md">
				<Grid align="end">
					<Grid.Col span={{ base: 12, md: 6 }}>
						<Select
							label={t("pages.development.form.repository", "Registered repository")}
							description={t(
								"pages.development.form.repositoryDescription",
								"The agent works in a managed worktree bound to this registered Git repository.",
							)}
							placeholder={t("pages.development.form.repositoryPlaceholder", "Select a repository")}
							data={repositoryOptions}
							value={values.selectedFolderId || null}
							onChange={(value) => {
								setValues((current) => ({ ...current, selectedFolderId: value ?? "" }));
								onRepositoryChange?.(value ?? "");
							}}
							loading={repositoriesLoading}
							disabled={repositoriesLoading || Boolean(repositoriesError)}
							required={true}
							data-testid="development-repository-select"
						/>
					</Grid.Col>
					<Grid.Col span={{ base: 12, md: 3 }}>
						<Button
							variant="light"
							leftSection={<IconFolderPlus size={16} />}
							onClick={() => {
								setRegistrationAttemptError(undefined);
								setRegistrationOpened(true);
							}}
							fullWidth={true}
							data-testid="development-open-register-repository"
						>
							{t("pages.development.form.registerRepository", "Register repository")}
						</Button>
					</Grid.Col>
					<Grid.Col span={{ base: 12, md: 3 }}>
						<Button
							variant="light"
							leftSection={<IconTemplate size={16} />}
							onClick={() => {
								setTemplateCreationError(undefined);
								setTemplateRegistryError(undefined);
								setTemplateOpened(true);
							}}
							fullWidth={true}
							data-testid="development-open-create-from-template"
						>
							{t("pages.development.form.createFromTemplate", "Create from template")}
						</Button>
					</Grid.Col>
				</Grid>
				{repositoriesError ? <InlineErrorAlert message={repositoriesError} /> : null}
				{detectionLoading ? (
					<Stack gap="xs" data-testid="development-profile-detecting">
						<Loader size="sm" aria-label={t("pages.development.profile.detectingLabel", "Detecting build system")} />
						<Text size="sm" c="dimmed">
							{t("pages.development.profile.detecting", "Inspecting the repository for a build system…")}
						</Text>
					</Stack>
				) : null}
				{detectionError ? <InlineErrorAlert message={detectionError} data-testid="development-profile-error" /> : null}
				{detection ? (
					<Stack gap="xs" data-testid="development-profile-confirmation">
						<Text size="sm" fw={600}>
							{t("pages.development.profile.title", "Confirm the command profile")}
						</Text>
						<Text size="sm" data-testid="development-profile-id">
							{t("pages.development.profile.detected", "Detected profile")}: {chosenProfileId}
						</Text>
						{candidates.length > 0 ? (
							<Select
								label={t("pages.development.profile.buildTarget", "Build target")}
								description={t(
									"pages.development.profile.buildTargetDescription",
									"The solution or project the validation gate restores, builds and tests.",
								)}
								data={candidates.map((candidate) => ({ value: candidate, label: candidate }))}
								value={chosenBuildTarget}
								onChange={(value) => setValues((current) => ({ ...current, buildTarget: value ?? undefined }))}
								data-testid="development-profile-build-target"
							/>
						) : null}
						{whitespaceOnly ? (
							<Alert color="orange" icon={<IconAlertTriangle size={16} />} data-testid="development-profile-whitespace-warning">
								<Text size="sm">
									{t(
										"pages.development.profile.whitespaceOnly",
										"No build system detected — validation will only check whitespace. Nothing will be restored, built or tested, so a passing validation does not mean the change compiles.",
									)}
								</Text>
							</Alert>
						) : null}
						<Checkbox
							checked={profileConfirmed}
							onChange={(event) => {
								const checked = event.currentTarget.checked;
								setProfileConfirmed(checked);
							}}
							label={t("pages.development.profile.confirm", "I confirm this command profile for the life of this project.")}
							data-testid="development-profile-confirm"
						/>
					</Stack>
				) : null}
				<Grid>
					<Grid.Col span={{ base: 12, md: 8 }}>
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
								setValues((current) => ({
									...current,
									egressPolicy: value === "CloudScoped" ? "CloudScoped" : "LocalOnly",
								}))
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
								setValues((current) => ({
									...current,
									maxDurationSeconds: typeof value === "number" ? value : undefined,
								}))
							}
						/>
					</Grid.Col>
				</Grid>
				{/*
				 * Provider-derived, not hard-coded. The two providers put the operator in materially different
				 * positions, and this notice sits directly on the control they must tick to proceed.
				 */}
				<Alert color={containerProvider ? "blue" : "yellow"} icon={<IconAlertTriangle size={16} />}>
					<Text size="sm" data-testid="development-security-notice">
						{containerProvider
							? t(
									"pages.development.form.securityWarningContainer",
									"Development commands run inside a hardened container — read-only root filesystem, all capabilities dropped, no host namespaces — with only the managed worktree and runtime directories mounted. Repository code still executes, so register only repositories you trust.",
								)
							: t(
									"pages.development.form.securityWarning",
									"Development commands run as your host user. The managed worktree confines application-mediated changes, but it is not OS isolation and repository code may access other host resources.",
								)}
					</Text>
				</Alert>
				<Checkbox
					checked={values.trustedRepositoryAcknowledged}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						setValues((current) => ({ ...current, trustedRepositoryAcknowledged: checked }));
					}}
					label={
						containerProvider
							? t(
									"pages.development.form.trustAcknowledgementContainer",
									"I trust the selected repository to execute Development commands inside this node's container sandbox.",
								)
							: t(
									"pages.development.form.trustAcknowledgement",
									"I trust the selected repository to execute Development commands with my host-user permissions.",
								)
					}
					data-testid="development-trust-acknowledgement"
				/>
				{error ? <div role="alert">{error}</div> : null}
				<Button
					type="submit"
					leftSection={<IconPlus size={16} />}
					loading={isSubmitting}
					disabled={!canCreate}
					data-testid="development-create-project"
				>
					{t("pages.development.form.create", "Create Development project")}
				</Button>
			</Stack>
		</form>
	);
}
