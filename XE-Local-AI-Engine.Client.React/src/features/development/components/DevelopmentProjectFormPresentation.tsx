import {
	Alert,
	Button,
	Checkbox,
	Divider,
	Grid,
	Group,
	Loader,
	NumberInput,
	Select,
	Stack,
	Text,
	Textarea,
	TextInput,
} from "@mantine/core";
import { IconAlertTriangle, IconFolderPlus, IconPlus, IconTemplate, IconTrash, IconX } from "@tabler/icons-react";
import type { TFunction } from "i18next";
import type { Dispatch, FormEvent, SetStateAction } from "react";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import type { DevelopmentProfileDetection, DevelopmentTemplate } from "@/features/development/models/DevelopmentModels";
import type {
	DevelopmentProjectFormValues,
	RegisterDevelopmentRepositoryValues,
	RegisterDevelopmentTemplateValues,
} from "@/features/development/models/DevelopmentProjectFormModels";

interface SelectOption {
	readonly value: string;
	readonly label: string;
	readonly disabled: boolean;
}

interface TemplateCreationValues {
	readonly templateId: string;
	readonly destinationPath: string;
	readonly alias: string;
}

interface ProjectDraftSectionProps {
	readonly containerProvider: boolean;
	readonly values: DevelopmentProjectFormValues;
	readonly setValues: Dispatch<SetStateAction<DevelopmentProjectFormValues>>;
	readonly repositoryOptions: readonly SelectOption[];
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

interface RepositoryRegistrationSectionProps {
	readonly registrationOpened: boolean;
	readonly setRegistrationOpened: Dispatch<SetStateAction<boolean>>;
	readonly registration: RegisterDevelopmentRepositoryValues;
	readonly setRegistration: Dispatch<SetStateAction<RegisterDevelopmentRepositoryValues>>;
	readonly registrationAttemptError?: string;
	readonly register: () => Promise<void>;
	readonly isRegistering: boolean;
}

interface TemplateRepositorySectionProps {
	readonly templateOpened: boolean;
	readonly setTemplateOpened: Dispatch<SetStateAction<boolean>>;
	readonly templateCreation: TemplateCreationValues;
	readonly setTemplateCreation: Dispatch<SetStateAction<TemplateCreationValues>>;
	readonly templateCreationError?: string;
	readonly templateCreating: boolean;
	readonly canCreateFromTemplate: boolean;
	readonly createFromTemplate: () => Promise<void>;
	readonly templateOptions: readonly SelectOption[];
	readonly templatesLoading: boolean;
	readonly templates: readonly DevelopmentTemplate[];
	readonly templateRegistryBusy: boolean;
	readonly templateRegistration: RegisterDevelopmentTemplateValues;
	readonly setTemplateRegistration: Dispatch<SetStateAction<RegisterDevelopmentTemplateValues>>;
	readonly templateRegistryError?: string;
	readonly addTemplate: () => Promise<void>;
	readonly removeTemplate: (templateId: string) => Promise<void>;
}

interface DevelopmentProjectFormPresentationProps {
	readonly t: TFunction;
	readonly project: ProjectDraftSectionProps;
	readonly repositoryRegistration: RepositoryRegistrationSectionProps;
	readonly templateRepository: TemplateRepositorySectionProps;
}

function ProjectDraftSection({ t, project: props }: Pick<DevelopmentProjectFormPresentationProps, "t" | "project">) {
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
				{repositoriesError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{repositoriesError}
					</Alert>
				) : null}
				{detectionLoading ? (
					<Stack gap="xs" data-testid="development-profile-detecting">
						<Loader size="sm" aria-label={t("pages.development.profile.detectingLabel", "Detecting build system")} />
						<Text size="sm" c="dimmed">
							{t("pages.development.profile.detecting", "Inspecting the repository for a build system…")}
						</Text>
					</Stack>
				) : null}
				{detectionError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="development-profile-error">
						{detectionError}
					</Alert>
				) : null}
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

function RepositoryRegistrationDialog({
	t,
	repositoryRegistration: props,
}: Pick<DevelopmentProjectFormPresentationProps, "t" | "repositoryRegistration">) {
	const {
		registrationOpened,
		setRegistrationOpened,
		registration,
		setRegistration,
		registrationAttemptError,
		register,
		isRegistering,
	} = props;
	return (
		<DialogShell
			opened={registrationOpened}
			onClose={() => setRegistrationOpened(false)}
			title={t("pages.development.register.title", "Register local Git repository")}
			confirmCloseWhen={registration.alias.length > 0 || registration.hostPath.length > 0}
			footer={
				<>
					<Button variant="subtle" leftSection={<IconX size={16} />} onClick={() => setRegistrationOpened(false)}>
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						leftSection={<IconFolderPlus size={16} />}
						onClick={register}
						loading={isRegistering}
						disabled={!registration.alias.trim() || !registration.hostPath.trim()}
						data-testid="development-register-repository"
					>
						{t("pages.development.register.submit", "Register repository")}
					</Button>
				</>
			}
		>
			<Stack gap="md" px="md" pb="md">
				<Text size="sm" c="dimmed">
					{t(
						"pages.development.register.description",
						"Enter the absolute path once. The server stores it encrypted and returns only this alias and an opaque identifier.",
					)}
				</Text>
				<TextInput
					label={t("pages.development.register.alias", "Alias")}
					value={registration.alias}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setRegistration((current) => ({ ...current, alias: value }));
					}}
					required={true}
					data-testid="development-register-alias"
				/>
				<TextInput
					label={t("pages.development.register.path", "Absolute repository path")}
					value={registration.hostPath}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setRegistration((current) => ({ ...current, hostPath: value }));
					}}
					required={true}
					data-testid="development-register-path"
				/>
				{registrationAttemptError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{registrationAttemptError}
					</Alert>
				) : null}
			</Stack>
		</DialogShell>
	);
}

function TemplateRepositoryDialog({
	t,
	templateRepository: props,
}: Pick<DevelopmentProjectFormPresentationProps, "t" | "templateRepository">) {
	const {
		templateOpened,
		setTemplateOpened,
		templateCreation,
		setTemplateCreation,
		templateCreationError,
		templateCreating,
		canCreateFromTemplate,
		createFromTemplate,
		templateOptions,
		templatesLoading,
		templates,
		templateRegistryBusy,
		templateRegistration,
		setTemplateRegistration,
		templateRegistryError,
		addTemplate,
		removeTemplate,
	} = props;
	return (
		<DialogShell
			opened={templateOpened}
			onClose={() => setTemplateOpened(false)}
			title={t("pages.development.template.title", "Create project repository from template")}
			confirmCloseWhen={templateCreation.destinationPath.length > 0 || templateCreation.alias.length > 0}
			footer={
				<>
					<Button variant="subtle" leftSection={<IconX size={16} />} onClick={() => setTemplateOpened(false)}>
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						leftSection={<IconTemplate size={16} />}
						onClick={createFromTemplate}
						loading={templateCreating}
						disabled={!canCreateFromTemplate}
						data-testid="development-create-from-template"
					>
						{t("pages.development.template.submit", "Create from template")}
					</Button>
				</>
			}
		>
			<Stack gap="md" px="md" pb="md">
				<Text size="sm" c="dimmed">
					{t(
						"pages.development.template.description",
						"The template repository is cloned to a new location on this host and registered as the project's repository.",
					)}
				</Text>
				<Select
					label={t("pages.development.template.template", "Template repository")}
					placeholder={t("pages.development.template.placeholder", "Select a template")}
					data={templateOptions}
					value={templateCreation.templateId || null}
					onChange={(value) => setTemplateCreation((current) => ({ ...current, templateId: value ?? "" }))}
					loading={templatesLoading}
					required={true}
					data-testid="development-template-select"
				/>
				<TextInput
					label={t("pages.development.template.destination", "Absolute destination path")}
					description={t(
						"pages.development.template.destinationDescription",
						"Where the clone is created. It must be an absolute path outside the node data directory.",
					)}
					value={templateCreation.destinationPath}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setTemplateCreation((current) => ({ ...current, destinationPath: value }));
					}}
					required={true}
					data-testid="development-template-destination"
				/>
				<TextInput
					label={t("pages.development.template.alias", "New project alias")}
					value={templateCreation.alias}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setTemplateCreation((current) => ({ ...current, alias: value }));
					}}
					required={true}
					data-testid="development-template-alias"
				/>
				{templateCreationError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{templateCreationError}
					</Alert>
				) : null}

				<Divider label={t("pages.development.template.registry", "Registered templates")} labelPosition="left" />
				{templatesLoading ? (
					<Loader size="sm" aria-label={t("pages.development.template.loadingLabel", "Loading registered templates")} />
				) : null}
				{!templatesLoading && templates.length === 0 ? (
					<Text size="sm" c="dimmed" data-testid="development-template-registry-empty">
						{t("pages.development.template.registryEmpty", "No template repositories are registered yet.")}
					</Text>
				) : null}
				{templates.map((template) => (
					<Group key={template.id} justify="space-between" wrap="nowrap">
						<Text size="sm">
							{template.alias}
							{template.availability === "Available" ? "" : ` — ${t("pages.development.templateUnavailable", "unavailable")}`}
						</Text>
						<Button
							variant="subtle"
							color="red"
							size="xs"
							leftSection={<IconTrash size={14} />}
							onClick={() => removeTemplate(template.id)}
							loading={templateRegistryBusy}
							data-testid="development-template-remove"
						>
							{t("pages.development.template.remove", "Remove")}
						</Button>
					</Group>
				))}
				<Grid align="end">
					<Grid.Col span={{ base: 12, md: 4 }}>
						<TextInput
							label={t("pages.development.template.registryAlias", "Template alias")}
							value={templateRegistration.alias}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setTemplateRegistration((current) => ({ ...current, alias: value }));
							}}
							data-testid="development-template-registry-alias"
						/>
					</Grid.Col>
					<Grid.Col span={{ base: 12, md: 5 }}>
						<TextInput
							label={t("pages.development.template.registryPath", "Absolute template path")}
							value={templateRegistration.hostPath}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setTemplateRegistration((current) => ({ ...current, hostPath: value }));
							}}
							data-testid="development-template-registry-path"
						/>
					</Grid.Col>
					<Grid.Col span={{ base: 12, md: 3 }}>
						<Button
							variant="light"
							leftSection={<IconPlus size={16} />}
							onClick={addTemplate}
							loading={templateRegistryBusy}
							disabled={!templateRegistration.alias.trim() || !templateRegistration.hostPath.trim()}
							fullWidth={true}
							data-testid="development-template-add"
						>
							{t("pages.development.template.add", "Add template")}
						</Button>
					</Grid.Col>
				</Grid>
				{templateRegistryError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{templateRegistryError}
					</Alert>
				) : null}
			</Stack>
		</DialogShell>
	);
}

export function DevelopmentProjectFormPresentation(props: DevelopmentProjectFormPresentationProps) {
	return (
		<>
			<ProjectDraftSection t={props.t} project={props.project} />
			<RepositoryRegistrationDialog t={props.t} repositoryRegistration={props.repositoryRegistration} />
			<TemplateRepositoryDialog t={props.t} templateRepository={props.templateRepository} />
		</>
	);
}
