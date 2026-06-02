import {
	ActionIcon,
	Alert,
	Badge,
	Button,
	Card,
	Code,
	Container,
	Group,
	Loader,
	Modal,
	Progress,
	ScrollArea,
	Select,
	SimpleGrid,
	Stack,
	Table,
	Text,
	TextInput,
	Title,
	Tooltip,
} from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconAlertTriangle, IconArrowBackUp, IconCheck, IconCloudDownload, IconFileText, IconRefresh, IconRobot, IconTrash } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import {
	deleteLocalModel,
	getLocalModelDetails,
	listLocalModels,
	pullLocalModel,
	resetLocalModelKind,
	selectLocalModel,
	setLocalModelKind,
} from "@/features/models/api/LocalModelsApi";
import { toLocalModelViewModel, toPullProgressModel } from "@/features/models/models/LocalModelModel";
import { localModelsQueryKeys } from "@/features/models/queries/LocalModelsQueryKeys";

/* eslint-disable react-doctor/no-event-handler, react-doctor/no-chain-state-updates */

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unexpected local model error";
}

// Effective-kind badge color: chat-capable models stand out (blue), embedding models are visually distinct (grape),
// and an unclassified/unknown model is muted (gray).
function kindBadgeColor(kind: string): string {
	switch (kind) {
		case "Chat":
			return "blue";
		case "Embedding":
			return "grape";
		default:
			return "gray";
	}
}

// Localized label for a raw Ollama capability string, falling back to the raw value for capabilities without a label.
function capabilityLabel(t: (key: string, fallback: string) => string, capability: string): string {
	switch (capability) {
		case "tools":
			return t("pages.models.type.capability.tools", "Tools");
		case "vision":
			return t("pages.models.type.capability.vision", "Vision");
		case "thinking":
			return t("pages.models.type.capability.thinking", "Thinking");
		default:
			return capability;
	}
}

// The three classifications the operator can pick from when overriding a model's kind (locked decision D1). Mirrors
// the persisted ModelKind enum names — the value is sent verbatim to the override endpoint.
const overridableKinds = ["Chat", "Embedding", "Unknown"] as const;

export function ModelManagement() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const { confirm } = useConfirm();
	const [selectedModelName, setSelectedModelName] = useState<string | undefined>();
	const [pullModelName, setPullModelName] = useState("");
	const [message, setMessage] = useState<string | undefined>();
	const [pullProgress, setPullProgress] = useState<number | undefined>();
	// License + template can both be very long; they live in a modal (not an inline expander) so the details card stays compact.
	const [detailsModalOpened, { open: openDetailsModal, close: closeDetailsModal }] = useDisclosure(false);

	const modelsQuery = useQuery({
		queryKey: localModelsQueryKeys.list(),
		queryFn: ({ signal }) => listLocalModels({ signal }),
	});
	const modelsResponse = modelsQuery.data;
	const modelViewModels = useMemo(() => modelsResponse?.items.map(toLocalModelViewModel) ?? [], [modelsResponse]);

	useEffect(() => {
		if (!modelsResponse) {
			return;
		}

		const preferredModelName = modelsResponse.selectedModelName ?? modelsResponse.items[0]?.modelName;
		const selectedStillExists = selectedModelName
			? modelsResponse.items.some((model) => model.modelName === selectedModelName)
			: false;
		if (!selectedStillExists) {
			setSelectedModelName(preferredModelName);
		}
	}, [modelsResponse, selectedModelName]);

	const detailsQuery = useQuery({
		queryKey: selectedModelName ? localModelsQueryKeys.details(selectedModelName) : localModelsQueryKeys.details(""),
		queryFn: ({ signal }) => getLocalModelDetails(selectedModelName ?? "", { signal }),
		enabled: Boolean(selectedModelName && modelsResponse?.isAvailable),
	});

	const selectMutation = useMutation({
		mutationFn: (modelName: string) => selectLocalModel({ modelName }),
		onSuccess: async (selection) => {
			setSelectedModelName(selection.selectedModelName);
			setMessage(`Default local model set to ${selection.selectedModelName}.`);
			await queryClient.invalidateQueries({ queryKey: localModelsQueryKeys.all() });
		},
	});

	const pullMutation = useMutation({
		mutationFn: () => pullLocalModel({ modelName: pullModelName.trim() }),
		onSuccess: async (response) => {
			const progress = toPullProgressModel(response);
			setPullProgress(progress.progressPercent);
			setPullModelName("");
			setMessage(`Model ${response.modelName} pull finished: ${progress.status}.`);
			await queryClient.invalidateQueries({ queryKey: localModelsQueryKeys.all() });
		},
	});

	const deleteMutation = useMutation({
		mutationFn: (modelName: string) => deleteLocalModel(modelName),
		onSuccess: async (response) => {
			setMessage(`Model ${response.modelName} deleted.`);
			setSelectedModelName(undefined);
			await queryClient.invalidateQueries({ queryKey: localModelsQueryKeys.all() });
		},
	});

	const setKindMutation = useMutation({
		mutationFn: ({ modelName, kind }: { modelName: string; kind: string }) => setLocalModelKind(modelName, kind),
		// Setting an override does NOT probe Ollama, so the response's detectedKind may still be Unknown. Invalidate
		// the list so the next refetch runs lazy detection and the row reflects the freshly detected kind, not the
		// override response.
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: localModelsQueryKeys.list() });
		},
	});

	const resetKindMutation = useMutation({
		mutationFn: (modelName: string) => resetLocalModelKind(modelName),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: localModelsQueryKeys.list() });
		},
	});

	const actionError = selectMutation.error ?? pullMutation.error ?? deleteMutation.error ?? setKindMutation.error ?? resetKindMutation.error;
	const isActionPending =
		selectMutation.isPending ||
		pullMutation.isPending ||
		deleteMutation.isPending ||
		setKindMutation.isPending ||
		resetKindMutation.isPending;
	const selectedModel = modelViewModels.find((model) => model.modelName === selectedModelName);
	const pullNameToSubmit = pullModelName.trim();

	// Localized label for a ModelKind enum string. Falls back to the raw value for any unexpected/future kind.
	const kindLabel = useCallback(
		(kind: string): string => {
			switch (kind) {
				case "Chat":
					return t("pages.models.type.kind.chat", "Chat");
				case "Embedding":
					return t("pages.models.type.kind.embedding", "Embedding");
				case "Unknown":
					return t("pages.models.type.kind.unknown", "Unknown");
				default:
					return kind;
			}
		},
		[t],
	);

	// Override options for a given model: the fixed overridable kinds, plus the model's current effective kind when it
	// is not already in the list. This keeps the Select's value (the effective model.kind) always matching an option, so
	// a future effective kind (e.g. Vision/Reranker) renders as a real, selectable entry instead of a blank Select.
	const kindSelectFor = useCallback(
		(currentKind: string): { value: string; label: string }[] => {
			const kinds = overridableKinds.some((kind) => kind === currentKind) ? overridableKinds : [...overridableKinds, currentKind];
			return kinds.map((kind) => ({ value: kind, label: kindLabel(kind) }));
		},
		[kindLabel],
	);

	const confirmDelete = useCallback(
		async (modelName: string) => {
			const confirmed = await confirm({
				title: "Delete model",
				description: `Delete '${modelName}' from the local model store? This cannot be undone.`,
				confirmationText: "Delete",
				cancellationText: "Cancel",
			});

			if (confirmed) {
				deleteMutation.mutate(modelName);
			}
		},
		[confirm, deleteMutation],
	);

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Group justify="space-between" align="flex-start">
					<Stack gap={4}>
						<Text size="sm" tt="uppercase" fw={700} c="dimmed">
							Worker Node
						</Text>
						<Title order={2}>Model management</Title>
						<Text c="dimmed">List, select, pull, and delete local Ollama models without changing runtime providers.</Text>
					</Stack>
					<Group gap="sm">
						<Badge color={modelsResponse?.isAvailable ? "green" : "red"}>
							{modelsResponse?.isAvailable ? "Ollama online" : "Ollama offline"}
						</Badge>
						<Button
							variant="subtle"
							leftSection={<IconRefresh size={16} />}
							onClick={() => modelsQuery.refetch()}
							disabled={modelsQuery.isFetching}
						>
							Refresh
						</Button>
					</Group>
				</Group>

				{modelsQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">Loading local models…</Text>
					</Group>
				) : null}

				{modelsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(modelsQuery.error)}
					</Alert>
				) : null}

				{modelsResponse && !modelsResponse.isAvailable ? (
					<Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
						{modelsResponse.error ?? "Local Ollama is not available. Start Ollama to list and manage models."}
					</Alert>
				) : null}

				{actionError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(actionError)}
					</Alert>
				) : null}

				{message ? <Alert color="green">{message}</Alert> : null}

				<SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
					<Card withBorder={true} radius="md" p="lg">
						<Stack gap="md">
							<Group justify="space-between">
								<Title order={3}>Installed models</Title>
								<IconRobot size={22} />
							</Group>
							<Table.ScrollContainer minWidth={680}>
								<Table striped={true} highlightOnHover={true} verticalSpacing="sm">
									<Table.Thead>
										<Table.Tr>
											<Table.Th>Name</Table.Th>
											<Table.Th>{t("pages.models.type.columnHeader", "Type")}</Table.Th>
											<Table.Th>Size</Table.Th>
											<Table.Th>Modified</Table.Th>
											<Table.Th>Family</Table.Th>
											<Table.Th>Quantization</Table.Th>
											<Table.Th>Actions</Table.Th>
										</Table.Tr>
									</Table.Thead>
									<Table.Tbody>
										{modelViewModels.map((model) => (
											<Table.Tr key={model.modelName}>
												<Table.Td>
													<Button variant="subtle" onClick={() => setSelectedModelName(model.modelName)}>
														{model.modelName}
													</Button>
													{model.isSelected ? (
														<Badge ml="xs" color="green">
															Default
														</Badge>
													) : null}
												</Table.Td>
												<Table.Td>
													<Stack gap={6}>
														<Group gap={6} align="center">
															<Badge
																color={kindBadgeColor(model.kind)}
																variant="light"
																data-testid={`model-kind-badge-${model.modelName}`}
															>
																{kindLabel(model.kind)}
															</Badge>
															{model.isOverridden ? (
																<Tooltip label={t("pages.models.type.reset", "Reset to detected")} withArrow={true}>
																	<ActionIcon
																		aria-label={`Reset ${model.modelName} type to detected`}
																		variant="subtle"
																		color="gray"
																		disabled={isActionPending}
																		onClick={() => resetKindMutation.mutate(model.modelName)}
																	>
																		<IconArrowBackUp size={16} />
																	</ActionIcon>
																</Tooltip>
															) : null}
														</Group>
														{model.capabilities.length > 0 ? (
															<Group gap={4}>
																{model.capabilities.map((capability) => (
																	<Badge key={capability} size="xs" variant="outline" color="gray">
																		{capabilityLabel(t, capability)}
																	</Badge>
																))}
															</Group>
														) : null}
														<Select
															aria-label={`Override type for ${model.modelName}`}
															data={kindSelectFor(model.kind)}
															value={model.kind}
															allowDeselect={false}
															disabled={isActionPending}
															size="xs"
															comboboxProps={{ withinPortal: false }}
															onChange={(value) => {
																if (value && value !== model.kind) {
																	setKindMutation.mutate({ modelName: model.modelName, kind: value });
																}
															}}
														/>
													</Stack>
												</Table.Td>
												<Table.Td>{model.sizeLabel}</Table.Td>
												<Table.Td>{model.modifiedDateLabel}</Table.Td>
												<Table.Td>{model.familyLabel}</Table.Td>
												<Table.Td>{model.quantizationLabel}</Table.Td>
												<Table.Td>
													<Group gap="xs">
														<Tooltip label="Set as default model" withArrow={true}>
															<ActionIcon
																aria-label={`Set ${model.modelName} as default`}
																variant="subtle"
																color="green"
																disabled={isActionPending}
																onClick={() => selectMutation.mutate(model.modelName)}
															>
																<IconCheck size={16} />
															</ActionIcon>
														</Tooltip>
														<Tooltip label="Delete model" withArrow={true}>
															<ActionIcon
																aria-label={`Delete ${model.modelName}`}
																variant="subtle"
																color="red"
																disabled={isActionPending}
																onClick={() => confirmDelete(model.modelName)}
															>
																<IconTrash size={16} />
															</ActionIcon>
														</Tooltip>
													</Group>
												</Table.Td>
											</Table.Tr>
										))}
									</Table.Tbody>
								</Table>
							</Table.ScrollContainer>
							{modelViewModels.length === 0 ? (
								<Text c="dimmed">{modelsResponse?.isAvailable ? "No local models found." : "Ollama is unavailable."}</Text>
							) : null}
						</Stack>
					</Card>

					<Card withBorder={true} radius="md" p="lg">
						<Stack gap="md">
							<Title order={3}>{selectedModel?.modelName ?? "Model details"}</Title>
							{detailsQuery.isFetching ? <Loader size="sm" /> : null}
							{selectedModel ? (
								<Stack gap="sm">
									<Text>Parameter size: {selectedModel.parameterSizeLabel}</Text>
									<Text>Family: {selectedModel.familyLabel}</Text>
									<Text>Quantization: {selectedModel.quantizationLabel}</Text>
									<Text>Context length: {detailsQuery.data?.maxContextTokens?.toLocaleString() ?? "Unknown"}</Text>
									{detailsQuery.data?.system ? <Alert color="blue">System prompt: {detailsQuery.data.system}</Alert> : null}
									{detailsQuery.data?.template || detailsQuery.data?.license ? (
										<Button
											variant="light"
											size="xs"
											leftSection={<IconFileText size={14} />}
											onClick={openDetailsModal}
											data-testid="model-license-template-button"
											style={{ alignSelf: "flex-start" }}
										>
											View license &amp; template
										</Button>
									) : null}
								</Stack>
							) : (
								<Text c="dimmed">Select an installed model to inspect details.</Text>
							)}
						</Stack>
					</Card>
				</SimpleGrid>

				<Modal
					opened={detailsModalOpened}
					onClose={closeDetailsModal}
					title={`${selectedModel?.modelName ?? "Model"} — license & template`}
					size="lg"
					scrollAreaComponent={ScrollArea.Autosize}
				>
					<Stack gap="lg">
						{detailsQuery.data?.template ? (
							<Stack gap={4}>
								<Text fw={600} size="sm">
									Template
								</Text>
								<Code block={true} style={{ whiteSpace: "pre-wrap" }} data-testid="model-template-content">
									{detailsQuery.data.template}
								</Code>
							</Stack>
						) : null}
						{detailsQuery.data?.license ? (
							<Stack gap={4}>
								<Text fw={600} size="sm">
									License
								</Text>
								<Code block={true} style={{ whiteSpace: "pre-wrap" }} data-testid="model-license-content">
									{detailsQuery.data.license}
								</Code>
							</Stack>
						) : null}
						{!detailsQuery.data?.template && !detailsQuery.data?.license ? (
							<Text c="dimmed">No license or template provided for this model.</Text>
						) : null}
					</Stack>
				</Modal>

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Title order={3}>Pull new model</Title>
						<TextInput
							data-testid="pull-model-name-input"
							label="Model name to pull"
							placeholder="orca-mini:latest"
							value={pullModelName}
							onChange={(event) => setPullModelName(event.currentTarget.value)}
							disabled={pullMutation.isPending}
						/>
						<Group>
							<Button
								data-testid="download-model-button"
								leftSection={<IconCloudDownload size={16} />}
								disabled={!pullNameToSubmit || isActionPending}
								loading={pullMutation.isPending}
								onClick={() => pullMutation.mutate()}
							>
								Download model
							</Button>
						</Group>
						{pullProgress !== undefined ? <Progress value={pullProgress} aria-label="Pull progress" /> : null}
					</Stack>
				</Card>
			</Stack>
		</Container>
	);
}
