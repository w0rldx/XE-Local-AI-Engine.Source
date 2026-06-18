import { Alert, Badge, Button, Card, Group, Loader, Stack, Table, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconPlayerEject, IconServer2 } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { RunningModel } from "@/features/model-fit/models/ModelFitModels";

interface RunningModelsPanelProps {
	runningModels: readonly RunningModel[];
	isLoading: boolean;
	error: unknown;
	onEject: (model: RunningModel) => void;
	ejectingModelName: string | null;
}

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

// Live list of running (loaded) local models with a per-row eject action. Server state is owned by the page's
// useRunningModels query and the eject mutation; this component is pure presentation. The in-flight download (when a
// GGUF is being fetched) surfaces here too as a non-responsive entry, with a cancel surfaced by the page elsewhere.
export function RunningModelsPanel({ runningModels, isLoading, error, onEject, ejectingModelName }: RunningModelsPanelProps) {
	const { t } = useTranslation();

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="model-fit-running-card">
			<Stack gap="md">
				<Group gap="xs" align="center">
					<IconServer2 size={20} />
					<Title order={4}>{t("pages.modelFit.running.title", "Running models")}</Title>
				</Group>

				{isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.modelFit.running.loading", "Loading running models…")}</Text>
					</Group>
				) : null}

				{error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="model-fit-running-error">
						{errorMessage(error, t("pages.modelFit.running.error", "Could not load running models."))}
					</Alert>
				) : null}

				{!isLoading && !error && runningModels.length === 0 ? (
					<Text c="dimmed" data-testid="model-fit-running-empty">
						{t("pages.modelFit.running.empty", "No models are currently running.")}
					</Text>
				) : null}

				{!isLoading && !error && runningModels.length > 0 ? (
					<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="model-fit-running-table">
						<Table.Thead>
							<Table.Tr>
								<Table.Th>{t("pages.modelFit.running.columns.model", "Model")}</Table.Th>
								<Table.Th>{t("pages.modelFit.running.columns.role", "Role")}</Table.Th>
								<Table.Th>{t("pages.modelFit.running.columns.status", "Status")}</Table.Th>
								<Table.Th>{t("pages.modelFit.running.columns.action", "Action")}</Table.Th>
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
												{t("pages.modelFit.running.responsive", "Responsive")}
											</Badge>
										) : (
											<Badge color="yellow" variant="light">
												{t("pages.modelFit.running.unresponsive", "Starting / unresponsive")}
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
											{t("pages.modelFit.running.eject", "Eject")}
										</Button>
									</Table.Td>
								</Table.Tr>
							))}
						</Table.Tbody>
					</Table>
				) : null}
			</Stack>
		</Card>
	);
}
