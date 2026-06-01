import {
	ActionIcon,
	Alert,
	Badge,
	Button,
	Card,
	Container,
	Group,
	Loader,
	Progress,
	SimpleGrid,
	Stack,
	Table,
	Text,
	TextInput,
	Title,
} from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconCloudDownload, IconRefresh, IconRobot, IconTrash } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useState } from "react";

import { ExpandableTextField } from "@/core/ui/components/ExpandableTextField/ExpandableTextField";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import {
	deleteLocalModel,
	getLocalModelDetails,
	listLocalModels,
	pullLocalModel,
	selectLocalModel,
} from "@/features/models/api/LocalModelsApi";
import { toLocalModelViewModel, toPullProgressModel } from "@/features/models/models/LocalModelModel";
import { localModelsQueryKeys } from "@/features/models/queries/LocalModelsQueryKeys";

/* eslint-disable react-doctor/no-event-handler, react-doctor/no-chain-state-updates */

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unexpected local model error";
}

export function ModelManagement() {
	const queryClient = useQueryClient();
	const { confirm } = useConfirm();
	const [selectedModelName, setSelectedModelName] = useState<string | undefined>();
	const [pullModelName, setPullModelName] = useState("");
	const [message, setMessage] = useState<string | undefined>();
	const [pullProgress, setPullProgress] = useState<number | undefined>();

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

	const actionError = selectMutation.error ?? pullMutation.error ?? deleteMutation.error;
	const isActionPending = selectMutation.isPending || pullMutation.isPending || deleteMutation.isPending;
	const selectedModel = modelViewModels.find((model) => model.modelName === selectedModelName);
	const pullNameToSubmit = pullModelName.trim();

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
												<Table.Td>{model.sizeLabel}</Table.Td>
												<Table.Td>{model.modifiedDateLabel}</Table.Td>
												<Table.Td>{model.familyLabel}</Table.Td>
												<Table.Td>{model.quantizationLabel}</Table.Td>
												<Table.Td>
													<Group gap="xs">
														<ActionIcon
															aria-label={`Set ${model.modelName} as default`}
															variant="subtle"
															color="green"
															disabled={isActionPending}
															onClick={() => selectMutation.mutate(model.modelName)}
														>
															<IconCheck size={16} />
														</ActionIcon>
														<ActionIcon
															aria-label={`Delete ${model.modelName}`}
															variant="subtle"
															color="red"
															disabled={isActionPending}
															onClick={() => confirmDelete(model.modelName)}
														>
															<IconTrash size={16} />
														</ActionIcon>
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
									{detailsQuery.data?.template ? (
										<Text style={{ whiteSpace: "pre-wrap" }}>Template: {detailsQuery.data.template}</Text>
									) : null}
									{detailsQuery.data?.system ? <Alert color="blue">System prompt: {detailsQuery.data.system}</Alert> : null}
									{detailsQuery.data?.license ? (
										<ExpandableTextField label="License" value={detailsQuery.data.license} />
									) : null}
								</Stack>
							) : (
								<Text c="dimmed">Select an installed model to inspect details.</Text>
							)}
						</Stack>
					</Card>
				</SimpleGrid>

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
