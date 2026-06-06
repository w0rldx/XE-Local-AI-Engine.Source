import { Alert, Badge, Button, Card, Container, Group, Loader, Stack, Table, Text, Title, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconInfoCircle, IconPlayerEject, IconServer } from "@tabler/icons-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { formatExpiresIn, formatLoadedModelSize } from "@/features/loaded-models/components/LoadedModelsFormatters";
import type { LoadedModel } from "@/features/loaded-models/models/LoadedModelsModels";
import { useEjectModel, useLoadedModels } from "@/features/loaded-models/queries/useLoadedModels";

// The "Expires in" countdown is derived against the current time, but the list only refetches on the poll cadence
// (several seconds). A lightweight 1s tick keeps the countdown visibly live between polls without refetching.
const countdownTickIntervalMs = 1000;

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

export function LoadedModelsPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const loadedModelsQuery = useLoadedModels();
	const ejectMutation = useEjectModel();

	const snapshot = loadedModelsQuery.data;
	const models = snapshot?.models ?? [];
	const isAvailable = snapshot?.isAvailable ?? false;

	// Tick once per second so the "Expires in" countdown recomputes live between the slower list polls. `now` only
	// feeds the per-row countdown derivation; the interval is cleared on unmount so it never leaks past the page.
	const [now, setNow] = useState<number>(() => Date.now());
	useEffect(() => {
		const intervalId = window.setInterval(() => setNow(Date.now()), countdownTickIntervalMs);
		return () => window.clearInterval(intervalId);
	}, []);

	const expiredLabel = t("pages.loadedModels.expired", "Expired");
	const sizeUnknownLabel = t("pages.loadedModels.sizeUnknown", "Unknown");

	const handleEject = async (modelName: string): Promise<void> => {
		const confirmed = await confirm({
			title: t("pages.loadedModels.eject.confirmTitle", "Eject model"),
			description: t(
				"pages.loadedModels.eject.confirmDescription",
				"Eject '{{modelName}}' from memory? Memory is freed after the current generation finishes; an in-flight response is not interrupted.",
				{ modelName },
			),
			confirmationText: t("pages.loadedModels.eject.confirmAction", "Eject"),
			cancellationText: t("common.cancel", "Cancel"),
		});

		if (!confirmed) {
			return;
		}

		ejectMutation.mutate(modelName, {
			onSuccess: () =>
				toast.success(
					t("pages.loadedModels.eject.success", "Ejecting '{{modelName}}' — memory frees after the current generation.", {
						modelName,
					}),
				),
			onError: (error) => toast.error(errorMessage(error, t("pages.loadedModels.eject.error", "Could not eject the model."))),
		});
	};

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Stack gap={4}>
					<Text size="sm" tt="uppercase" fw={700} c="dimmed">
						{t("pages.loadedModels.eyebrow", "Worker Node")}
					</Text>
					<Group gap="xs" align="center">
						<IconServer size={24} />
						<Title order={2}>{t("pages.loadedModels.title", "Loaded models")}</Title>
					</Group>
					<Text c="dimmed">
						{t(
							"pages.loadedModels.subtitle",
							"Models the local runtime is currently holding in memory (RAM/VRAM). Eject frees that memory — gracefully, after any in-flight generation completes.",
						)}
					</Text>
				</Stack>

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						{loadedModelsQuery.isLoading ? (
							<Group gap="sm" data-testid="loaded-models-loading">
								<Loader size="sm" />
								<Text c="dimmed">{t("pages.loadedModels.loading", "Loading loaded models…")}</Text>
							</Group>
						) : null}

						{loadedModelsQuery.error ? (
							<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="loaded-models-error">
								{errorMessage(loadedModelsQuery.error, t("pages.loadedModels.errors.load", "Could not load loaded models."))}
							</Alert>
						) : null}

						{!loadedModelsQuery.isLoading && !loadedModelsQuery.error && !isAvailable ? (
							<Alert color="gray" icon={<IconInfoCircle size={16} />} data-testid="loaded-models-unavailable">
								{snapshot?.error ??
									t(
										"pages.loadedModels.unavailable",
										"The local model runtime is unavailable right now. This view will update once it is reachable.",
									)}
							</Alert>
						) : null}

						{!loadedModelsQuery.isLoading && !loadedModelsQuery.error && isAvailable && models.length === 0 ? (
							<Text c="dimmed" data-testid="loaded-models-empty">
								{t("pages.loadedModels.empty", "No models currently loaded.")}
							</Text>
						) : null}

						{!loadedModelsQuery.isLoading && !loadedModelsQuery.error && isAvailable && models.length > 0 ? (
							<Table.ScrollContainer minWidth={640}>
								<Table verticalSpacing="sm" data-testid="loaded-models-table">
									<Table.Thead>
										<Table.Tr>
											<Table.Th>{t("pages.loadedModels.columns.model", "Model")}</Table.Th>
											<Table.Th>{t("pages.loadedModels.columns.memory", "Memory (RAM)")}</Table.Th>
											<Table.Th>{t("pages.loadedModels.columns.vram", "VRAM")}</Table.Th>
											<Table.Th>{t("pages.loadedModels.columns.expiresIn", "Expires in")}</Table.Th>
											<Table.Th>{t("pages.loadedModels.columns.action", "Action")}</Table.Th>
										</Table.Tr>
									</Table.Thead>
									<Table.Tbody>
										{models.map((model: LoadedModel) => {
											const ejectingThisRow = ejectMutation.isPending && ejectMutation.variables === model.modelName;
											return (
												<Table.Tr key={model.modelName} data-testid={`loaded-models-row-${model.modelName}`}>
													<Table.Td>
														<Text ff="monospace">{model.modelName}</Text>
													</Table.Td>
													<Table.Td>
														{model.sizeBytes === null ? (
															<Badge variant="light" color="gray">
																{sizeUnknownLabel}
															</Badge>
														) : (
															formatLoadedModelSize(model.sizeBytes)
														)}
													</Table.Td>
													<Table.Td>
														{model.sizeVramBytes === null ? (
															<Badge variant="light" color="gray">
																{t("pages.loadedModels.cpuOnly", "CPU only")}
															</Badge>
														) : (
															formatLoadedModelSize(model.sizeVramBytes)
														)}
													</Table.Td>
													<Table.Td>{formatExpiresIn(model.expiresAtUtc, expiredLabel, now)}</Table.Td>
													<Table.Td>
														<Tooltip
															label={t("pages.loadedModels.eject.tooltip", "Frees memory after the current generation finishes")}
														>
															<Button
																variant="light"
																color="red"
																size="xs"
																leftSection={<IconPlayerEject size={16} />}
																loading={ejectingThisRow}
																onClick={() => handleEject(model.modelName)}
																data-testid={`loaded-models-eject-${model.modelName}`}
															>
																{t("pages.loadedModels.eject.button", "Eject")}
															</Button>
														</Tooltip>
													</Table.Td>
												</Table.Tr>
											);
										})}
									</Table.Tbody>
								</Table>
							</Table.ScrollContainer>
						) : null}
					</Stack>
				</Card>
			</Stack>
		</Container>
	);
}
