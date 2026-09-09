import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type { DevelopmentTemplate } from "@/features/development/models/DevelopmentModels";
import type {
	CreateDevelopmentRepositoryFromTemplateValues,
	CreatedDevelopmentRepositoryFromTemplate,
	RegisterDevelopmentTemplateValues,
	TemplateCreationValues,
} from "@/features/development/models/DevelopmentProjectFormModels";

const emptyTemplateCreation: TemplateCreationValues = { templateId: "", destinationPath: "", alias: "" };

interface UseTemplateRepositoryRegistryOptions {
	readonly templates: readonly DevelopmentTemplate[];
	readonly baseBranch: string;
	readonly onCreateFromTemplate?: (
		values: CreateDevelopmentRepositoryFromTemplateValues,
	) => Promise<CreatedDevelopmentRepositoryFromTemplate>;
	readonly onAddTemplate?: (values: RegisterDevelopmentTemplateValues) => Promise<DevelopmentTemplate>;
	readonly onRemoveTemplate?: (templateId: string) => Promise<void>;
	readonly onRepositoryChange?: (selectedFolderId: string) => void;
	readonly selectRepository: (repositoryId: string) => void;
}

/** The "create from template" dialog: the clone draft, the template registry (add/remove), and their busy/error state. */
export function useTemplateRepositoryRegistry({
	templates,
	baseBranch,
	onCreateFromTemplate,
	onAddTemplate,
	onRemoveTemplate,
	onRepositoryChange,
	selectRepository,
}: UseTemplateRepositoryRegistryOptions) {
	const { t } = useTranslation();
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
				baseBranch,
			});
			selectRepository(created.repository.id);

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

	return {
		templateOpened,
		setTemplateOpened,
		templateCreation,
		setTemplateCreation,
		templateCreationError,
		setTemplateCreationError,
		templateCreating,
		canCreateFromTemplate,
		createFromTemplate,
		templateOptions,
		templateRegistryBusy,
		templateRegistration,
		setTemplateRegistration,
		templateRegistryError,
		setTemplateRegistryError,
		addTemplate,
		removeTemplate,
	};
}
