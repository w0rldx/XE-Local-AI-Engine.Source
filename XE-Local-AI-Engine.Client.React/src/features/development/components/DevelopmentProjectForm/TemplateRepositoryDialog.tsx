import { Button, Divider, Grid, Group, Loader, Select, Stack, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTemplate, IconTrash, IconX } from "@tabler/icons-react";
import type { TFunction } from "i18next";
import type { Dispatch, SetStateAction } from "react";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import type { DevelopmentTemplate } from "@/features/development/models/DevelopmentModels";
import type {
	DevelopmentFormSelectOption,
	RegisterDevelopmentTemplateValues,
	TemplateCreationValues,
} from "@/features/development/models/DevelopmentProjectFormModels";

export interface TemplateRepositorySectionProps {
	readonly templateOpened: boolean;
	readonly setTemplateOpened: Dispatch<SetStateAction<boolean>>;
	readonly templateCreation: TemplateCreationValues;
	readonly setTemplateCreation: Dispatch<SetStateAction<TemplateCreationValues>>;
	readonly templateCreationError?: string;
	readonly templateCreating: boolean;
	readonly canCreateFromTemplate: boolean;
	readonly createFromTemplate: () => Promise<void>;
	readonly templateOptions: readonly DevelopmentFormSelectOption[];
	readonly templatesLoading: boolean;
	readonly templates: readonly DevelopmentTemplate[];
	readonly templateRegistryBusy: boolean;
	readonly templateRegistration: RegisterDevelopmentTemplateValues;
	readonly setTemplateRegistration: Dispatch<SetStateAction<RegisterDevelopmentTemplateValues>>;
	readonly templateRegistryError?: string;
	readonly addTemplate: () => Promise<void>;
	readonly removeTemplate: (templateId: string) => Promise<void>;
}

export function TemplateRepositoryDialog({
	t,
	templateRepository: props,
}: {
	readonly t: TFunction;
	readonly templateRepository: TemplateRepositorySectionProps;
}) {
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
				{templateCreationError ? <InlineErrorAlert message={templateCreationError} /> : null}

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
				{templateRegistryError ? <InlineErrorAlert message={templateRegistryError} /> : null}
			</Stack>
		</DialogShell>
	);
}
