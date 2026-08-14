import { Alert, Badge, Button, Group, Loader, Table, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconPlayerEject, IconServer } from "@tabler/icons-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { formatExpiresIn, formatLoadedModelSize } from "@/features/loaded-models/components/LoadedModelsFormatters";
import { RunningModelsPanel } from "@/features/loaded-models/components/RunningModelsPanel";
import type { LoadedModel } from "@/features/loaded-models/models/LoadedModelsModels";
import type { RunningModel } from "@/features/loaded-models/models/RunningModelsModels";
import { useEjectModel, useLoadedModels } from "@/features/loaded-models/queries/useLoadedModels";
import { useEjectRunningModel, useRunningModels } from "@/features/loaded-models/queries/useRunningModels";

// The "Expires in" countdown is derived against the current time, but the list only refetches on the poll cadence
// (several seconds). A lightweight 1s tick keeps the countdown visibly live between polls without refetching.
const countdownTickIntervalMs = 1000;

export function LoadedModelsPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const loadedModelsQuery = useLoadedModels();
	const ejectMutation = useEjectModel();

	// llama.cpp running models — a DIFFERENT runtime from the Ollama in-memory list above. Relocated from the model-fit
	// advisor so both runtimes show side by side.
	const runningModelsQuery = useRunningModels();
	const ejectRunningMutation = useEjectRunningModel();

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
			onError: (error) => toast.error(apiErrorMessage(error, t("pages.loadedModels.eject.error", "Could not eject the model."))),
		});
	};

	// Ejects a llama.cpp running model (separate runtime / endpoint from the Ollama eject above). A graceful eject
	// (force=false) waits for any in-flight generation to drain; the outcome (AUD4-20) drives a distinct toast, and a
	// timed-out-still-busy result offers a force eject that interrupts the run.
	const handleEjectRunning = async (model: RunningModel, force = false): Promise<void> => {
		if (!force) {
			const confirmed = await confirm({
				title: t("pages.loadedModels.llamaCpp.ejectConfirm.title", "Eject model"),
				description: t(
					"pages.loadedModels.llamaCpp.ejectConfirm.description",
					"Eject '{{modelName}}' from the llama.cpp runtime? Memory frees after any in-flight generation completes; a running response is not interrupted.",
					{ modelName: model.modelName },
				),
				confirmationText: t("pages.loadedModels.llamaCpp.ejectConfirm.action", "Eject"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (!confirmed) {
				return;
			}
		}

		ejectRunningMutation.mutate(
			{ modelName: model.modelName, role: model.role || undefined, force },
			{
				onSuccess: async (result) => {
					switch (result.outcome) {
						case "ejected":
							toast.success(t("pages.loadedModels.llamaCpp.ejectOutcome.ejected", "Model ejected."));
							return;
						case "forced":
							toast.info(
								t("pages.loadedModels.llamaCpp.ejectOutcome.forced", "Model ejected — the in-flight generation was interrupted."),
							);
							return;
						case "not_running":
							toast.info(t("pages.loadedModels.llamaCpp.ejectOutcome.notRunning", "That model was not running."));
							return;
						case "timed_out_still_busy": {
							toast.warning(
								t(
									"pages.loadedModels.llamaCpp.ejectOutcome.timedOutStillBusy",
									"'{{modelName}}' is still finishing a response, so it was left running.",
									{ modelName: model.modelName },
								),
							);
							const forceConfirmed = await confirm({
								title: t("pages.loadedModels.llamaCpp.forceConfirm.title", "Force eject?"),
								description: t(
									"pages.loadedModels.llamaCpp.forceConfirm.description",
									"'{{modelName}}' is still generating. Force-eject anyway and interrupt the running turn?",
									{ modelName: model.modelName },
								),
								confirmationText: t("pages.loadedModels.llamaCpp.forceConfirm.action", "Force eject"),
								cancellationText: t("common.cancel", "Cancel"),
							});
							if (forceConfirmed) {
								await handleEjectRunning(model, true);
							}
							return;
						}
						default:
							return;
					}
				},
				onError: (error) =>
					toast.error(apiErrorMessage(error, t("pages.loadedModels.llamaCpp.ejectError", "Could not eject the model."))),
			},
		);
	};

	return (
		<PageShell>
			<PageHeader
				icon={<IconServer size={24} />}
				title={t("pages.loadedModels.title", "Loaded models")}
				subtitle={t(
					"pages.loadedModels.subtitle",
					"Models the local runtime is currently holding in memory (RAM/VRAM). Eject frees that memory — gracefully, after any in-flight generation completes.",
				)}
			/>

			<SectionCard title={t("pages.loadedModels.ollama.title", "Ollama (in-memory)")} icon={<IconServer size={22} />}>
				{loadedModelsQuery.isLoading ? (
					<Group gap="sm" data-testid="loaded-models-loading">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.loadedModels.loading", "Loading loaded models…")}</Text>
					</Group>
				) : null}

				{loadedModelsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="loaded-models-error">
						{apiErrorMessage(loadedModelsQuery.error, t("pages.loadedModels.errors.load", "Could not load loaded models."))}
					</Alert>
				) : null}

				{!loadedModelsQuery.isLoading && !loadedModelsQuery.error && !isAvailable ? (
					// Ollama is an optional secondary provider, deliberately absent on the desktop default. An
					// unreachable provider is therefore an expected empty state, not an error: render a neutral,
					// dimmed line (never a red/warning alert) and do NOT surface the raw connection-refused reason
					// as an alarming banner. A genuine transport/shape failure still routes to the error alert above.
					<EmptyState
						data-testid="loaded-models-unavailable"
						message={t(
							"pages.loadedModels.unavailable",
							"Ollama isn't reachable right now. It's an optional secondary provider, so there may be nothing loaded to show here. llama.cpp models still appear below.",
						)}
					/>
				) : null}

				{!loadedModelsQuery.isLoading && !loadedModelsQuery.error && isAvailable && models.length === 0 ? (
					<EmptyState data-testid="loaded-models-empty" message={t("pages.loadedModels.empty", "No models currently loaded.")} />
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
			</SectionCard>

			<RunningModelsPanel
				runningModels={runningModelsQuery.data ?? []}
				isLoading={runningModelsQuery.isLoading}
				error={runningModelsQuery.error}
				onEject={handleEjectRunning}
				ejectingModelName={ejectRunningMutation.isPending ? (ejectRunningMutation.variables?.modelName ?? null) : null}
			/>
		</PageShell>
	);
}
