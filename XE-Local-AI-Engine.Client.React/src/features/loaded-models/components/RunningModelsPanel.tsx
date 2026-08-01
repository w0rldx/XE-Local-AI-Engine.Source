import { Alert, Badge, Button, Card, Group, Loader, Stack, Table, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconPlayerEject, IconServer2 } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type { RunningModel } from "@/features/loaded-models/models/RunningModelsModels";

interface RunningModelsPanelProps {
	runningModels: readonly RunningModel[];
	isLoading: boolean;
	error: unknown;
	onEject: (model: RunningModel) => void;
	ejectingModelName: string | null;
}

// Live list of running (loaded) llama.cpp models with a per-row eject action. Server state is owned by the page's
// useRunningModels query and the eject mutation; this component is pure presentation. The in-flight download (when a
// GGUF is being fetched on the Model Management page) surfaces here too as a non-responsive entry.
export function RunningModelsPanel({ runningModels, isLoading, error, onEject, ejectingModelName }: RunningModelsPanelProps) {
	const { t } = useTranslation();

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="model-fit-running-card">
			<Stack gap="md">
				<Group gap="xs" align="center">
					<IconServer2 size={20} />
					<Title order={4}>{t("pages.loadedModels.llamaCpp.title", "llama.cpp runtime")}</Title>
				</Group>

				{isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.loadedModels.llamaCpp.loading", "Loading running models…")}</Text>
					</Group>
				) : null}

				{error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="model-fit-running-error">
						{apiErrorMessage(error, t("pages.loadedModels.llamaCpp.error", "Could not load running models."))}
					</Alert>
				) : null}

				{!isLoading && !error && runningModels.length === 0 ? (
					<Text c="dimmed" data-testid="model-fit-running-empty">
						{t("pages.loadedModels.llamaCpp.empty", "No models are currently running.")}
					</Text>
				) : null}

				{!isLoading && !error && runningModels.length > 0 ? (
					<Table.ScrollContainer minWidth={600}>
						<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="model-fit-running-table">
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("pages.loadedModels.llamaCpp.columns.model", "Model")}</Table.Th>
									<Table.Th>{t("pages.loadedModels.llamaCpp.columns.role", "Role")}</Table.Th>
									<Table.Th>{t("pages.loadedModels.llamaCpp.columns.status", "Status")}</Table.Th>
									<Table.Th>{t("pages.loadedModels.llamaCpp.columns.action", "Action")}</Table.Th>
								</Table.Tr>
							</Table.Thead>
							<Table.Tbody>
								{runningModels.map((model) => (
									<Table.Tr key={`${model.modelName}-${model.role}`} data-testid={`model-fit-running-row-${model.modelName}`}>
										<Table.Td>
											<Text size="sm" fw={500}>
												{model.modelName}
											</Text>
											{model.detail ? (
												<Text size="xs" c="dimmed">
													{model.detail}
												</Text>
											) : null}
										</Table.Td>
										<Table.Td>{model.role ? <Badge variant="outline">{model.role}</Badge> : "—"}</Table.Td>
										<Table.Td>
											{model.isResponsive ? (
												<Badge color="green" variant="light">
													{t("pages.loadedModels.llamaCpp.responsive", "Responsive")}
												</Badge>
											) : (
												<Badge color="yellow" variant="light">
													{t("pages.loadedModels.llamaCpp.unresponsive", "Starting / unresponsive")}
												</Badge>
											)}
										</Table.Td>
										<Table.Td>
											<Button
												size="xs"
												variant="light"
												color="red"
												leftSection={<IconPlayerEject size={14} />}
												loading={ejectingModelName === model.modelName}
												disabled={ejectingModelName === model.modelName}
												onClick={() => onEject(model)}
												data-testid={`model-fit-eject-button-${model.modelName}`}
											>
												{t("pages.loadedModels.llamaCpp.eject", "Eject")}
											</Button>
										</Table.Td>
									</Table.Tr>
								))}
							</Table.Tbody>
						</Table>
					</Table.ScrollContainer>
				) : null}
			</Stack>
		</Card>
	);
}
