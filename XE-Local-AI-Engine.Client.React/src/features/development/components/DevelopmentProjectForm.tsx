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
import { type FormEvent, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import {
	type DevelopmentProfileDetection,
	developmentProfileIdForBuildTarget,
	type DevelopmentRepository,
	type DevelopmentTemplate,
	isDevelopmentWhitespaceOnlyProfile,
} from "@/features/development/models/DevelopmentModels";

export interface RegisterDevelopmentRepositoryValues {
	readonly alias: string;
	readonly hostPath: string;
}

export interface RegisterDevelopmentTemplateValues {
	readonly alias: string;
	readonly hostPath: string;
}

export interface CreateDevelopmentRepositoryFromTemplateValues {
	readonly templateId: string;
	readonly destinationPath: string;
	readonly alias: string;
	readonly baseBranch: string;
}

export interface CreatedDevelopmentRepositoryFromTemplate {
	readonly repository: DevelopmentRepository;
	readonly templateAlias?: string;
	readonly templateCommit?: string;
}

export interface DevelopmentProjectFormValues {
	readonly selectedFolderId: string;
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
	/** The profile the operator confirmed. Omitted when detection never loaded, which asks the server to detect. */
	readonly commandProfileId?: string;
	readonly buildTarget?: string;
}

interface DevelopmentProjectFormProps {
	readonly repositories: readonly DevelopmentRepository[];
	readonly repositoriesLoading: boolean;
	readonly repositoriesError?: string;
	readonly isRegistering: boolean;
	readonly isSubmitting: boolean;
	readonly error?: string;
	readonly detection?: DevelopmentProfileDetection | null;
	readonly detectionLoading?: boolean;
	readonly detectionError?: string;
	readonly templates?: readonly DevelopmentTemplate[];
	readonly templatesLoading?: boolean;
	readonly onRepositoryChange?: (selectedFolderId: string) => void;
	readonly onRegister: (values: RegisterDevelopmentRepositoryValues) => Promise<DevelopmentRepository>;
	readonly onCreateFromTemplate?: (
		values: CreateDevelopmentRepositoryFromTemplateValues,
	) => Promise<CreatedDevelopmentRepositoryFromTemplate>;
	readonly onAddTemplate?: (values: RegisterDevelopmentTemplateValues) => Promise<DevelopmentTemplate>;
	readonly onRemoveTemplate?: (templateId: string) => Promise<void>;
	readonly onSubmit: (values: DevelopmentProjectFormValues) => void;
}

interface TemplateCreationValues {
	readonly templateId: string;
	readonly destinationPath: string;
	readonly alias: string;
}

const emptyTemplateCreation: TemplateCreationValues = { templateId: "", destinationPath: "", alias: "" };

const initialValues: DevelopmentProjectFormValues = {
	selectedFolderId: "",
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

export function DevelopmentProjectForm({
	repositories,
	repositoriesLoading,
	repositoriesError,
	isRegistering,
	isSubmitting,
	error,
	detection,
	detectionLoading = false,
	detectionError,
	templates = [],
	templatesLoading = false,
	onRepositoryChange,
	onRegister,
	onCreateFromTemplate,
	onAddTemplate,
	onRemoveTemplate,
	onSubmit,
}: DevelopmentProjectFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState(initialValues);
	const [profileConfirmed, setProfileConfirmed] = useState(false);
	const [confirmedDetectionIdentity, setConfirmedDetectionIdentity] = useState<string | null>(null);
	const [registrationOpened, setRegistrationOpened] = useState(false);
	const [registration, setRegistration] = useState<RegisterDevelopmentRepositoryValues>({ alias: "", hostPath: "" });
	const [registrationAttemptError, setRegistrationAttemptError] = useState<string>();
	const [templateOpened, setTemplateOpened] = useState(false);
	const [templateCreation, setTemplateCreation] = useState<TemplateCreationValues>(emptyTemplateCreation);
	const [templateCreationError, setTemplateCreationError] = useState<string>();
	const [templateCreating, setTemplateCreating] = useState(false);
	const [templateRegistration, setTemplateRegistration] = useState<RegisterDevelopmentTemplateValues>({
		alias: "",
		hostPath: "",
	});
	const [templateRegistryError, setTemplateRegistryError] = useState<string>();
	const [templateRegistryBusy, setTemplateRegistryBusy] = useState(false);
	const repositoryOptions = useMemo(
		() =>
			repositories.map((repository) => ({
				value: repository.id,
				label:
					repository.availability === "Available"
						? repository.alias
						: `${repository.alias} (${t("pages.development.repositoryUnavailable", "unavailable")})`,
				disabled: repository.availability !== "Available",
			})),
		[repositories, t],
	);

	const templateOptions = useMemo(
		() =>
			templates.map((template) => ({
				value: template.id,
				label:
					template.availability === "Available"
						? template.alias
						: `${template.alias} (${t("pages.development.templateUnavailable", "unavailable")})`,
				disabled: template.availability !== "Available",
			})),
		[templates, t],
	);

	const selectedRepository = repositories.find((repository) => repository.id === values.selectedFolderId);

	const detectedProfileId = detection?.profileId ?? null;
	const candidates = useMemo(() => detection?.candidates ?? [], [detection]);
	// The chosen target defaults to what detection proposed and moves the profile with it, because the backend pairs
	// the two strictly.
	const chosenBuildTarget = values.buildTarget ?? detection?.buildTarget ?? null;
	const chosenProfileId = detection
		? chosenBuildTarget
			? developmentProfileIdForBuildTarget(chosenBuildTarget)
			: detectedProfileId
		: null;
	const whitespaceOnly = Boolean(detection) && isDevelopmentWhitespaceOnlyProfile(chosenProfileId);

	// A new detection is a new proposal, so neither a previous confirmation nor a previous override can carry over to
	// it. Adjusted during render rather than in an effect: an effect would let a stale confirmation be visible — and
	// therefore submittable — for one paint after the proposal changed.
	const detectionIdentity = detection ? `${detection.profileId ?? ""}:${detection.buildTarget ?? ""}` : null;
	if (detectionIdentity !== confirmedDetectionIdentity) {
		setConfirmedDetectionIdentity(detectionIdentity);
		setProfileConfirmed(false);
		setValues((current) => ({ ...current, commandProfileId: undefined, buildTarget: undefined }));
	}

	// Detection is advisory: when it has not loaded (or failed) the server runs its own detection at creation, so the
	// confirmation gate only applies once there is something concrete to confirm.
	const canCreate =
		values.trustedRepositoryAcknowledged &&
		selectedRepository?.availability === "Available" &&
		!repositoriesLoading &&
		(!detection || profileConfirmed);

	const submit = (event: FormEvent<HTMLFormElement>): void => {
		event.preventDefault();
		onSubmit(
			chosenProfileId
				? { ...values, commandProfileId: chosenProfileId, buildTarget: chosenBuildTarget ?? undefined }
				: values,
		);
	};

	const register = async (): Promise<void> => {
		setRegistrationAttemptError(undefined);
		try {
			const created = await onRegister(registration);
			setValues((current) => ({ ...current, selectedFolderId: created.id }));

			// Registering auto-selects the new repository, so the owner has to be told as well — this is the same
			// notification the Select's onChange fires. Without it the page's profileFolderId stays null on the
			// register-then-create path, detection never runs, and the command-profile confirmation step never
			// appears at all. That is the FIRST-RUN path, so the operator would simply never be shown the profile.
			onRepositoryChange?.(created.id);
			setRegistration({ alias: "", hostPath: "" });
			setRegistrationOpened(false);
		} catch (registrationFailure) {
			setRegistrationAttemptError(
				registrationFailure instanceof Error
					? registrationFailure.message
					: t("pages.development.register.error", "Could not register the local Git repository."),
			);
		}
	};

	const createFromTemplate = async (): Promise<void> => {
		if (!onCreateFromTemplate) {
			return;
		}

		setTemplateCreationError(undefined);
		setTemplateCreating(true);
		try {
			const created = await onCreateFromTemplate({
				templateId: templateCreation.templateId,
				destinationPath: templateCreation.destinationPath,
				alias: templateCreation.alias,
				baseBranch: values.baseBranch,
			});
			setValues((current) => ({ ...current, selectedFolderId: created.repository.id }));

			// Same contract as the register path, and the same defect if it is dropped: creating from a template
			// auto-selects the new repository, so the owner has to be told too. Without this the page's profileFolderId
			// stays null, detection never runs, and the command-profile confirmation step never appears on what is
			// again a FIRST-RUN path.
			onRepositoryChange?.(created.repository.id);
			setTemplateCreation(emptyTemplateCreation);
			setTemplateOpened(false);
		} catch (creationFailure) {
			setTemplateCreationError(
				creationFailure instanceof Error
					? creationFailure.message
					: t("pages.development.template.error", "Could not create the project repository from the template."),
			);
		} finally {
			setTemplateCreating(false);
		}
	};

	const addTemplate = async (): Promise<void> => {
		if (!onAddTemplate) {
			return;
		}

		setTemplateRegistryError(undefined);
		setTemplateRegistryBusy(true);
		try {
			await onAddTemplate({ alias: templateRegistration.alias, hostPath: templateRegistration.hostPath });
			setTemplateRegistration({ alias: "", hostPath: "" });
		} catch (registryFailure) {
			setTemplateRegistryError(
				registryFailure instanceof Error
					? registryFailure.message
					: t("pages.development.template.registryError", "Could not register the template repository."),
			);
		} finally {
			setTemplateRegistryBusy(false);
		}
	};

	const removeTemplate = async (templateId: string): Promise<void> => {
		if (!onRemoveTemplate) {
			return;
		}

		setTemplateRegistryError(undefined);
		setTemplateRegistryBusy(true);
		try {
			await onRemoveTemplate(templateId);
			setTemplateCreation((current) => (current.templateId === templateId ? emptyTemplateCreation : current));
		} catch (registryFailure) {
			setTemplateRegistryError(
				registryFailure instanceof Error
					? registryFailure.message
					: t("pages.development.template.removeError", "Could not remove the template repository."),
			);
		} finally {
			setTemplateRegistryBusy(false);
		}
	};

	const canCreateFromTemplate = Boolean(
		templateCreation.templateId.trim() && templateCreation.destinationPath.trim() && templateCreation.alias.trim(),
	);

	return (
		<>
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
							<Loader size="sm" aria-label="Detecting build system" />
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
									onChange={(value) =>
										setValues((current) => ({ ...current, buildTarget: value ?? undefined }))
									}
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
								label={t(
									"pages.development.profile.confirm",
									"I confirm this command profile for the life of this project.",
								)}
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
					<Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
						<Text size="sm">
							{t(
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
						label={t(
							"pages.development.form.trustAcknowledgement",
							"I trust the selected repository to execute Development commands with my host-user permissions.",
						)}
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

					<Divider
						label={t("pages.development.template.registry", "Registered templates")}
						labelPosition="left"
					/>
					{templatesLoading ? <Loader size="sm" aria-label="Loading registered templates" /> : null}
					{!templatesLoading && templates.length === 0 ? (
						<Text size="sm" c="dimmed" data-testid="development-template-registry-empty">
							{t("pages.development.template.registryEmpty", "No template repositories are registered yet.")}
						</Text>
					) : null}
					{templates.map((template) => (
						<Group key={template.id} justify="space-between" wrap="nowrap">
							<Text size="sm">
								{template.alias}
								{template.availability === "Available"
									? ""
									: ` — ${t("pages.development.templateUnavailable", "unavailable")}`}
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
		</>
	);
}
