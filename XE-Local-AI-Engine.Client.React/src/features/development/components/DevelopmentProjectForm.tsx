import { Alert, Button, Checkbox, Grid, Loader, NumberInput, Select, Stack, Text, Textarea, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconFolderPlus, IconPlus, IconX } from "@tabler/icons-react";
import { type FormEvent, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import {
	type DevelopmentProfileDetection,
	developmentProfileIdForBuildTarget,
	type DevelopmentRepository,
	isDevelopmentWhitespaceOnlyProfile,
} from "@/features/development/models/DevelopmentModels";

export interface RegisterDevelopmentRepositoryValues {
	readonly alias: string;
	readonly hostPath: string;
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
	readonly onRepositoryChange?: (selectedFolderId: string) => void;
	readonly onRegister: (values: RegisterDevelopmentRepositoryValues) => Promise<DevelopmentRepository>;
	readonly onSubmit: (values: DevelopmentProjectFormValues) => void;
}

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
	onRepositoryChange,
	onRegister,
	onSubmit,
}: DevelopmentProjectFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState(initialValues);
	const [profileConfirmed, setProfileConfirmed] = useState(false);
	const [confirmedDetectionIdentity, setConfirmedDetectionIdentity] = useState<string | null>(null);
	const [registrationOpened, setRegistrationOpened] = useState(false);
	const [registration, setRegistration] = useState<RegisterDevelopmentRepositoryValues>({ alias: "", hostPath: "" });
	const [registrationAttemptError, setRegistrationAttemptError] = useState<string>();
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

	return (
		<>
			<form onSubmit={submit} data-testid="development-project-form">
				<Stack gap="md">
					<Grid align="end">
						<Grid.Col span={{ base: 12, md: 8 }}>
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
						<Grid.Col span={{ base: 12, md: 4 }}>
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
		</>
	);
}
