import { Button, Card, Group, Select, Stack, Text, Title } from "@mantine/core";
import { IconCloudDownload, IconDatabase } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import {
	nodeSettingsFieldError,
	nodeSettingsFieldLabel,
} from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import type { NodeSettingsFieldsForm, NodeSettingsModelOption } from "@/features/node-settings/models/NodeSettingsFieldsModel";

interface Props {
	readonly form: NodeSettingsFieldsForm;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
	readonly rerankerModelOptions: readonly NodeSettingsModelOption[];
	readonly embeddingModelOptions: readonly NodeSettingsModelOption[];
	readonly rerankerDownload: { readonly onStart: () => void; readonly pending: boolean; readonly inFlight: boolean };
	readonly embeddingDownload: { readonly onStart: () => void; readonly pending: boolean; readonly inFlight: boolean };
}
export function NodeSettingsKnowledgeModelsCard({
	form,
	errors,
	onChange,
	rerankerModelOptions,
	embeddingModelOptions,
	rerankerDownload,
	embeddingDownload,
}: Props) {
	const { t } = useTranslation();
	const rerankerOptions = useMemo(() => {
		const options = [{ value: "", label: t("pages.nodeSettings.fields.rerankerModel.off", "Off") }, ...rerankerModelOptions];
		if (form.rerankerModelName !== "" && !options.some((option) => option.value === form.rerankerModelName)) {
			options.push({ value: form.rerankerModelName, label: form.rerankerModelName });
		}
		return options;
	}, [rerankerModelOptions, form.rerankerModelName, t]);
	// A pin whose model is gone stays visible under its name, never silently blank.
	const embeddingOptions = useMemo(() => {
		const options = [
			{ value: "", label: t("pages.nodeSettings.fields.embeddingModel.unpinned", "Not pinned yet (set by the first index)") },
			...embeddingModelOptions,
		];
		if (form.knowledgeEmbeddingModelName !== "" && !options.some((option) => option.value === form.knowledgeEmbeddingModelName)) {
			options.push({ value: form.knowledgeEmbeddingModelName, label: form.knowledgeEmbeddingModelName });
		}
		return options;
	}, [embeddingModelOptions, form.knowledgeEmbeddingModelName, t]);
	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-knowledge-models-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={2} size="h4">
						{t("pages.nodeSettings.fields.knowledgeModels.title", "Knowledge models")}
					</Title>
					<IconDatabase size={20} />
				</Group>
				<Group justify="space-between" align="center" wrap="nowrap" gap="md">
					<Text size="xs" c="dimmed">
						{t(
							"pages.nodeSettings.fields.embeddingModel.recommendedHelp",
							"Required for the knowledge base: without an embedding model, documents cannot be indexed. Recommended: nomic-embed-text-v1.5 (~274 MB).",
						)}
					</Text>
					<Button
						variant="light"
						size="xs"
						leftSection={<IconCloudDownload size={14} />}
						onClick={embeddingDownload.onStart}
						loading={embeddingDownload.pending}
						disabled={embeddingDownload.pending || embeddingDownload.inFlight}
						data-testid="node-settings-embedding-download-recommended"
					>
						{t("pages.nodeSettings.fields.embeddingModel.downloadRecommended", "Download recommended embedding model")}
					</Button>
				</Group>
				<Select
					label={nodeSettingsFieldLabel(
						t,
						"knowledgeEmbeddingModelName",
						t("pages.nodeSettings.fields.embeddingModel.label", "Embedding model"),
					)}
					description={t(
						"pages.nodeSettings.fields.embeddingModel.description",
						"The model every knowledge-base document and query is embedded with. The node pins the model that indexes the first document, so installing another embedding model never switches it. Changing it here marks every document indexed with another model for reindexing.",
					)}
					data={embeddingOptions}
					value={form.knowledgeEmbeddingModelName}
					onChange={(value) => onChange("knowledgeEmbeddingModelName", value ?? "")}
					allowDeselect={false}
					searchable={true}
					nothingFoundMessage={t("pages.nodeSettings.fields.embeddingModel.empty", "No installed embedding models")}
					error={nodeSettingsFieldError(t, errors, "knowledgeEmbeddingModelName")}
					data-testid="node-settings-embedding-model"
				/>
				<Select
					label={nodeSettingsFieldLabel(
						t,
						"rerankerModelName",
						t("pages.nodeSettings.fields.rerankerModel.label", "Reranker model"),
					)}
					description={t(
						"pages.nodeSettings.fields.rerankerModel.description",
						"Cross-encoder reranker that reorders knowledge-base search results for relevance. Leave off if no reranker model is installed. Loaded only when memory allows; its VRAM is counted by capacity checks and model recommendations.",
					)}
					data={rerankerOptions}
					value={form.rerankerModelName}
					onChange={(value) => onChange("rerankerModelName", value ?? "")}
					allowDeselect={false}
					searchable={true}
					nothingFoundMessage={t("pages.nodeSettings.fields.rerankerModel.empty", "No installed models")}
					error={nodeSettingsFieldError(t, errors, "rerankerModelName")}
					data-testid="node-settings-reranker-model"
				/>
				<Group justify="space-between" align="center" wrap="nowrap" gap="md">
					<Text size="xs" c="dimmed">
						{t(
							"pages.nodeSettings.fields.rerankerModel.recommendedHelp",
							"Recommended: bge-reranker-v2-m3, which runs as its own extra model server.",
						)}
					</Text>
					<Button
						variant="light"
						size="xs"
						leftSection={<IconCloudDownload size={14} />}
						onClick={rerankerDownload.onStart}
						loading={rerankerDownload.pending}
						disabled={rerankerDownload.pending || rerankerDownload.inFlight}
						data-testid="node-settings-reranker-download-recommended"
					>
						{t("pages.nodeSettings.fields.rerankerModel.downloadRecommended", "Download recommended reranker")}
					</Button>
				</Group>
			</Stack>
		</Card>
	);
}
