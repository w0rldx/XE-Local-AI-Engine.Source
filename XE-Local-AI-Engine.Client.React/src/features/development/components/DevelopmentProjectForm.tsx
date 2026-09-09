import { useTranslation } from "react-i18next";

import { DevelopmentProjectFormPresentation } from "@/features/development/components/DevelopmentProjectFormPresentation";
import { useProjectDraft } from "@/features/development/hooks/useProjectDraft";
import { useRepositoryRegistration } from "@/features/development/hooks/useRepositoryRegistration";
import { useTemplateRepositoryRegistry } from "@/features/development/hooks/useTemplateRepositoryRegistry";
import {
	type DevelopmentProfileDetection,
	type DevelopmentRepository,
	type DevelopmentTemplate,
	isDevelopmentContainerProvider,
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

const emptyTemplates: readonly DevelopmentTemplate[] = [];

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
	const draft = useProjectDraft({ repositories, repositoriesLoading, detection, onSubmit });
	const registration = useRepositoryRegistration({
		onRegister,
		onRepositoryChange,
		selectRepository: draft.selectRepository,
	});
	const templateRegistry = useTemplateRepositoryRegistry({
		templates,
		baseBranch: draft.values.baseBranch,
		onCreateFromTemplate,
		onAddTemplate,
		onRemoveTemplate,
		onRepositoryChange,
		selectRepository: draft.selectRepository,
	});

	return (
		<DevelopmentProjectFormPresentation
			t={t}
			project={{
				containerProvider,
				values: draft.values,
				setValues: draft.setValues,
				repositoryOptions: draft.repositoryOptions,
				repositoriesLoading,
				repositoriesError,
				onRepositoryChange,
				detection,
				detectionLoading,
				detectionError,
				candidates: draft.candidates,
				chosenBuildTarget: draft.chosenBuildTarget,
				chosenProfileId: draft.chosenProfileId,
				whitespaceOnly: draft.whitespaceOnly,
				profileConfirmed: draft.profileConfirmed,
				setProfileConfirmed: draft.setProfileConfirmed,
				canCreate: draft.canCreate,
				isSubmitting,
				error,
				submit: draft.submit,
				setRegistrationOpened: registration.setRegistrationOpened,
				setRegistrationAttemptError: registration.setRegistrationAttemptError,
				setTemplateOpened: templateRegistry.setTemplateOpened,
				setTemplateCreationError: templateRegistry.setTemplateCreationError,
				setTemplateRegistryError: templateRegistry.setTemplateRegistryError,
			}}
			repositoryRegistration={{
				registrationOpened: registration.registrationOpened,
				setRegistrationOpened: registration.setRegistrationOpened,
				registration: registration.registration,
				setRegistration: registration.setRegistration,
				registrationAttemptError: registration.registrationAttemptError,
				register: registration.register,
				isRegistering,
			}}
			templateRepository={{
				templateOpened: templateRegistry.templateOpened,
				setTemplateOpened: templateRegistry.setTemplateOpened,
				templateCreation: templateRegistry.templateCreation,
				setTemplateCreation: templateRegistry.setTemplateCreation,
				templateCreationError: templateRegistry.templateCreationError,
				templateCreating: templateRegistry.templateCreating,
				canCreateFromTemplate: templateRegistry.canCreateFromTemplate,
				createFromTemplate: templateRegistry.createFromTemplate,
				templateOptions: templateRegistry.templateOptions,
				templatesLoading,
				templates,
				templateRegistryBusy: templateRegistry.templateRegistryBusy,
				templateRegistration: templateRegistry.templateRegistration,
				setTemplateRegistration: templateRegistry.setTemplateRegistration,
				templateRegistryError: templateRegistry.templateRegistryError,
				addTemplate: templateRegistry.addTemplate,
				removeTemplate: templateRegistry.removeTemplate,
			}}
		/>
	);
}
