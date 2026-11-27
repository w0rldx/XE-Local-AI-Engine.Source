import { Badge, Card, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconPlayerEject, IconServer2 } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { formatCount } from "@/core/formatting/NumberFormatting";
import type { RunningModel } from "@/core/loaded-models/models/RunningModelsModels";
import { FocusableDisabledButton } from "@/core/ui/components/FocusableDisabledButton/FocusableDisabledButton";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import { useEjectFocusRestore } from "@/features/loaded-models/hooks/useEjectFocusRestore";

interface RunningModelsPanelProps {
	runningModels: readonly RunningModel[];
	isLoading: boolean;
	error: unknown;
	/**
	 * Opens a confirmation first and may await the request. `restoreFocus` gives lost focus back (the Eject, else the
	 * heading); pass it as each dialog's `onClosed`, the only point the panel restores focus.
	 */
	onEject: (model: RunningModel, restoreFocus: () => void) => Promise<void> | void;
	ejectingModelName: string | null;
}

// Live list of running (loaded) llama.cpp models with a per-row eject action. Server state is owned by the page's
// useRunningModels query and the eject mutation; this component is pure presentation. The in-flight download (when a
// GGUF is being fetched on the Model Management page) surfaces here too as a non-responsive entry.
//
// The test ids are `loaded-models-llamacpp-*`: page-scoped and runtime-qualified. They were `model-fit-running-*` until
// 2026-08-01, vestigial names from the model-fit advisor page this panel was relocated from (a test asserts it is absent
// there now).
export function RunningModelsPanel({ runningModels, isLoading, error, onEject, ejectingModelName }: RunningModelsPanelProps) {
	const { t } = useTranslation();
	const rowKeys = runningModels.map((model) => `${model.modelName}-${model.role}`).join("|");
	const { headingRef, cardFocusHandlers, restoreFocusTo } = useEjectFocusRestore(rowKeys);

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="loaded-models-llamacpp-card" {...cardFocusHandlers}>
			<Stack gap="md">
				<Group gap="xs" align="center">
					<IconServer2 size={20} />
					<Title ref={headingRef} tabIndex={-1} order={2} size="h4">
						{t("pages.loadedModels.llamaCpp.title", "llama.cpp runtime")}
					</Title>
				</Group>

				{isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.loadedModels.llamaCpp.loading", "Loading running models…")}</Text>
					</Group>
				) : null}

				{error ? (
					<InlineErrorAlert
						message={apiErrorMessage(error, t("pages.loadedModels.llamaCpp.error", "Could not load running models."))}
						data-testid="loaded-models-llamacpp-error"
					/>
				) : null}

				{!isLoading && !error && runningModels.length === 0 ? (
					<Text c="dimmed" data-testid="loaded-models-llamacpp-empty">
						{t("pages.loadedModels.llamaCpp.empty", "No models are currently running.")}
					</Text>
				) : null}

				{!isLoading && !error && runningModels.length > 0 ? (
					<ResponsiveTable
						rows={runningModels}
						rowKey={(model) => `${model.modelName}-${model.role}`}
						rowTestId={(model) => `loaded-models-llamacpp-row-${model.modelName}`}
						minWidth={600}
						data-testid="loaded-models-llamacpp-table"
						columns={[
							{
								key: "model",
								header: t("pages.loadedModels.llamaCpp.columns.model", "Model"),
								primary: true,
								cell: (model) => (
									<>
										<Text size="sm" fw={500} style={{ overflowWrap: "anywhere" }}>
											{model.modelName}
										</Text>
										{model.detailCode !== null || model.detail ? (
											<Text size="xs" c="dimmed" data-testid={`loaded-models-llamacpp-detail-${model.modelName}`}>
												{model.detailCode === null
													? model.detail
													: t(`pages.loadedModels.llamaCpp.details.${model.detailCode}`, model.detail)}
											</Text>
										) : null}
										{model.effectiveContextTokens !== null ? (
											<Text size="xs" c="dimmed" data-testid={`loaded-models-llamacpp-context-${model.modelName}`}>
												{t("pages.loadedModels.llamaCpp.contextWindow", {
													defaultValue: "Context window: {{tokens}} tokens",
													tokens: formatCount(model.effectiveContextTokens),
												})}
											</Text>
										) : null}
										{model.expertsOffloaded ? (
											<Badge
												mt={4}
												color="orange"
												variant="light"
												title={t(
													"pages.loadedModels.llamaCpp.expertsOffloadedHint",
													"This mixture-of-experts model keeps its expert weights in system RAM instead of on the GPU, so it generates noticeably slower.",
												)}
												data-testid={`loaded-models-llamacpp-experts-offloaded-${model.modelName}`}
											>
												{t("pages.loadedModels.llamaCpp.expertsOffloaded", "Experts in system RAM")}
											</Badge>
										) : null}
									</>
								),
							},
							{
								key: "role",
								header: t("pages.loadedModels.llamaCpp.columns.role", "Role"),
								cell: (model) => (model.role ? <Badge variant="outline">{model.role}</Badge> : "—"),
							},
							{
								key: "status",
								header: t("pages.loadedModels.llamaCpp.columns.status", "Status"),
								cell: (model) =>
									model.isResponsive ? (
										<Badge color="green" variant="light" style={{ minWidth: "max-content" }}>
											{t("pages.loadedModels.llamaCpp.responsive", "Responsive")}
										</Badge>
									) : (
										<Badge color="yellow" variant="light" style={{ minWidth: "max-content" }}>
											{t("pages.loadedModels.llamaCpp.unresponsive", "Starting / unresponsive")}
										</Badge>
									),
							},
						]}
						actionsHeader={t("pages.loadedModels.llamaCpp.columns.action", "Action")}
						actions={(model) => (
							<FocusableDisabledButton
								size="xs"
								variant="light"
								color="red"
								leftSection={<IconPlayerEject size={14} />}
								pending={ejectingModelName === model.modelName}
								onClick={async (event) => {
									await onEject(model, restoreFocusTo(event.currentTarget));
								}}
								data-testid={`loaded-models-llamacpp-eject-${model.modelName}`}
							>
								{t("pages.loadedModels.llamaCpp.eject", "Eject")}
							</FocusableDisabledButton>
						)}
					/>
				) : null}
			</Stack>
		</Card>
	);
}
