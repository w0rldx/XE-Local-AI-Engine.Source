import { type FormEvent, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DevelopmentProjectFormPresentation } from "@/features/development/components/DevelopmentProjectFormPresentation";
import {
	type DevelopmentProfileDetection,
	type DevelopmentRepository,
	type DevelopmentTemplate,
	developmentProfileIdForBuildTarget,
	isDevelopmentContainerProvider,
	isDevelopmentWhitespaceOnlyProfile,
} from "@/features/development/models/DevelopmentModels";
import type {
	CreateDevelopmentRepositoryFromTemplateValues,
	CreatedDevelopmentRepositoryFromTemplate,
	DevelopmentProjectFormValues,
	RegisterDevelopmentRepositoryValues,
	RegisterDevelopmentTemplateValues,
} from "@/features/development/models/DevelopmentProjectFormModels";

export type {
	CreateDevelopmentRepositoryFromTemplateValues,
	CreatedDevelopmentRepositoryFromTemplate,
	DevelopmentProjectFormValues,
	RegisterDevelopmentRepositoryValues,
	RegisterDevelopmentTemplateValues,
} from "@/features/development/models/DevelopmentProjectFormModels";

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
	/**
	 * The sandbox provider actually resolved for this node. Drives the safety notice and the trust acknowledgement,
	 * which describe two different isolation postures and must not be hard-coded to either.
	 */
	readonly sandboxProvider?: string;
}

interface TemplateCreationValues {
	readonly templateId: string;
	readonly destinationPath: string;
	readonly alias: string;
}

const emptyTemplateCreation: TemplateCreationValues = { templateId: "", destinationPath: "", alias: "" };
const emptyTemplates: readonly DevelopmentTemplate[] = [];

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
	templates = emptyTemplates,
	templatesLoading = false,
	onRepositoryChange,
	onRegister,
	onCreateFromTemplate,
	onAddTemplate,
	onRemoveTemplate,
	onSubmit,
	sandboxProvider,
}: DevelopmentProjectFormProps) {
	const { t } = useTranslation();
	const containerProvider = isDevelopmentContainerProvider(sandboxProvider);
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
			chosenProfileId ? { ...values, commandProfileId: chosenProfileId, buildTarget: chosenBuildTarget ?? undefined } : values,
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
				apiErrorMessage(
					registrationFailure,
					t("pages.development.register.error", "Could not register the local Git repository."),
				),
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
				apiErrorMessage(
					creationFailure,
					t("pages.development.template.error", "Could not create the project repository from the template."),
				),
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
				apiErrorMessage(
					registryFailure,
					t("pages.development.template.registryError", "Could not register the template repository."),
				),
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
				apiErrorMessage(
					registryFailure,
					t("pages.development.template.removeError", "Could not remove the template repository."),
				),
			);
		} finally {
			setTemplateRegistryBusy(false);
		}
	};

	const canCreateFromTemplate = Boolean(
		templateCreation.templateId.trim() && templateCreation.destinationPath.trim() && templateCreation.alias.trim(),
	);

	return (
		<DevelopmentProjectFormPresentation
			t={t}
			project={{
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
			}}
			repositoryRegistration={{
				registrationOpened,
				setRegistrationOpened,
				registration,
				setRegistration,
				registrationAttemptError,
				register,
				isRegistering,
			}}
			templateRepository={{
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
			}}
		/>
	);
}
