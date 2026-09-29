import { Anchor, Badge, Button, Group, Popover, Progress, Stack, Text, UnstyledButton } from "@mantine/core";
import { IconPlayerEject, IconStack2 } from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { formatBytesAsGb } from "@/core/formatting/BytesFormatting";
import { DESKTOP_NAV_BREAKPOINT } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { toast } from "@/core/ui/notifications/Toast";
import type { RunningModel } from "@/features/loaded-models/models/RunningModelsModels";
import { useEjectRunningModel, useRunningModels } from "@/features/loaded-models/queries/useRunningModels";
import { HIGH_USAGE_PERCENT, type MemoryUsage, usagePercent } from "@/features/runtime-resources/models/RuntimeResourcesModels";
import { useRuntimeResources } from "@/features/runtime-resources/queries/useRuntimeResources";

type ResidentState = "exited" | "unresponsive" | "busy" | "transient" | "idle";

function residentState(model: RunningModel): ResidentState {
	if (model.detailCode === "exited") {
		return "exited";
	}
	if (!model.isResponsive) {
		return "unresponsive";
	}
	if (model.isBusy) {
		return "busy";
	}
	return model.isTransient ? "transient" : "idle";
}

const residentStateColors: Record<ResidentState, string> = {
	exited: "gray",
	unresponsive: "yellow",
	busy: "blue",
	transient: "grape",
	idle: "green",
};

function barColor(usage: MemoryUsage): string {
	return usagePercent(usage) >= HIGH_USAGE_PERCENT ? "red" : "blue";
}

function UsageRow({ label, usage }: { label: string; usage: MemoryUsage }) {
	const { t } = useTranslation();

	return (
		<Stack gap={4}>
			<Group justify="space-between" gap="xs">
				<Text size="sm" fw={500}>
					{label}
				</Text>
				<Text size="xs" c="dimmed">
					{t("runtimeResources.usage", "{{used}} used of {{total}}, {{available}} available", {
						used: formatBytesAsGb(usage.usedBytes),
						total: formatBytesAsGb(usage.totalBytes),
						available: formatBytesAsGb(usage.availableBytes),
					})}
				</Text>
			</Group>
			<Progress value={usagePercent(usage)} color={barColor(usage)} aria-label={label} />
		</Stack>
	);
}

// Always-visible gauge in the header: whole-machine RAM and VRAM plus the llama.cpp models the engine holds, with a
// graceful eject. Force eject stays on the Loaded Models page. Rendered only for a signed-in operator session on a
// node with the model-fit surface, because both endpoints sit behind the Operator policy.
export function RuntimeResourcesWidget() {
	const { t } = useTranslation();
	const isAuthenticated = useNodeAuthStore((state) => Boolean(state.accessToken));
	// Desktop only this round: below the breakpoint the header hides it, so it must not poll there either.
	const { width } = useWindowDimensions();
	const enabled = nodeCapabilities.modelFit && isAuthenticated && width >= DESKTOP_NAV_BREAKPOINT;
	const resourcesQuery = useRuntimeResources(enabled);
	const runningModelsQuery = useRunningModels(enabled);
	const ejectMutation = useEjectRunningModel();
	const [opened, setOpened] = useState(false);

	const resources = resourcesQuery.data;
	if (!enabled || !resources) {
		return null;
	}

	const residents = runningModelsQuery.data ?? [];
	const firstGpu = resources.gpus[0];
	// Keyed by model AND role: the same model can run as chat and as embedding at once.
	const ejecting = ejectMutation.isPending ? ejectMutation.variables : undefined;
	const isEjecting = (model: RunningModel) => ejecting?.modelName === model.modelName && (ejecting.role ?? "") === model.role;

	// Graceful only: a busy model is never interrupted from here. The outcomes mirror the Loaded Models page.
	const handleEject = (model: RunningModel) =>
		ejectMutation.mutate(
			{ modelName: model.modelName, role: model.role || undefined, force: false },
			{
				onSuccess: (result) => {
					switch (result.outcome) {
						case "ejected":
							toast.success(t("pages.loadedModels.llamaCpp.ejectOutcome.ejected", "Model ejected."));
							return;
						case "not_running":
							toast.info(t("pages.loadedModels.llamaCpp.ejectOutcome.notRunning", "That model was not running."));
							return;
						case "timed_out_still_busy":
							toast.warning(
								t(
									"pages.loadedModels.llamaCpp.ejectOutcome.timedOutStillBusy",
									"'{{modelName}}' is still finishing a response, so it was left running.",
									{ modelName: model.modelName },
								),
							);
							return;
						default:
							return;
					}
				},
				onError: (error) =>
					toast.error(apiErrorMessage(error, t("pages.loadedModels.llamaCpp.ejectError", "Could not eject the model."))),
			},
		);

	return (
		<Popover
			opened={opened}
			onChange={setOpened}
			position="bottom-start"
			shadow="md"
			width={360}
			trapFocus={true}
			withinPortal={true}
		>
			<Popover.Target>
				<UnstyledButton
					type="button"
					className="flex flex-row items-center gap-3 px-2 py-1"
					aria-label={t("runtimeResources.trigger", "Memory usage and loaded models")}
					aria-expanded={opened}
					onClick={() => setOpened((previous) => !previous)}
					data-testid="runtime-resources-trigger"
				>
					<Group gap={6} wrap="nowrap">
						<Text size="xs" c="dimmed">
							{t("runtimeResources.ram", "RAM")}
						</Text>
						<Progress.Root w={56}>
							<Progress.Section value={usagePercent(resources.ram)} color={barColor(resources.ram)} withAria={false} />
						</Progress.Root>
					</Group>
					{firstGpu ? (
						<Group gap={6} wrap="nowrap">
							<Text size="xs" c="dimmed">
								{t("runtimeResources.vram", "VRAM")}
							</Text>
							<Progress.Root w={56}>
								<Progress.Section value={usagePercent(firstGpu)} color={barColor(firstGpu)} withAria={false} />
							</Progress.Root>
						</Group>
					) : null}
					<Group gap={4} wrap="nowrap">
						<IconStack2 size={14} />
						<Text size="xs" data-testid="runtime-resources-count">
							{residents.length}
						</Text>
					</Group>
				</UnstyledButton>
			</Popover.Target>
			<Popover.Dropdown data-testid="runtime-resources-dropdown">
				<Stack gap="sm">
					<UsageRow label={t("runtimeResources.ram", "RAM")} usage={resources.ram} />
					{resources.gpus.length === 0 ? (
						<Text size="sm" c="dimmed" data-testid="runtime-resources-vram-unavailable">
							{t("runtimeResources.vramUnavailable", "VRAM usage unavailable")}
						</Text>
					) : (
						resources.gpus.map((gpu) => (
							<UsageRow
								key={gpu.index}
								label={t("runtimeResources.gpu", "GPU {{index}} VRAM", { index: gpu.index })}
								usage={gpu}
							/>
						))
					)}

					<Text size="sm" fw={500}>
						{t("runtimeResources.residents", "Loaded llama.cpp models")}
					</Text>
					{residents.length === 0 ? (
						<Text size="sm" c="dimmed">
							{t("runtimeResources.noResidents", "No models are loaded.")}
						</Text>
					) : (
						residents.map((model) => {
							const state = residentState(model);
							return (
								<Group
									key={`${model.modelName}-${model.role}`}
									justify="space-between"
									wrap="nowrap"
									gap="xs"
									data-testid={`runtime-resources-row-${model.modelName}`}
								>
									<Stack gap={2} style={{ minWidth: 0 }}>
										<Text size="sm" truncate="end">
											{model.modelName}
										</Text>
										<Group gap={4}>
											{model.role ? (
												<Badge size="xs" variant="outline">
													{model.role}
												</Badge>
											) : null}
											<Badge size="xs" variant="light" color={residentStateColors[state]}>
												{t(`runtimeResources.state.${state}`)}
											</Badge>
										</Group>
									</Stack>
									<Button
										size="compact-xs"
										variant="light"
										color="red"
										leftSection={<IconPlayerEject size={12} />}
										loading={isEjecting(model)}
										disabled={model.isBusy || isEjecting(model)}
										onClick={() => handleEject(model)}
										aria-label={t("runtimeResources.ejectLabel", "Eject {{modelName}}", { modelName: model.modelName })}
										data-testid={`runtime-resources-eject-${model.modelName}`}
									>
										{t("pages.loadedModels.llamaCpp.eject", "Eject")}
									</Button>
								</Group>
							);
						})
					)}

					<Anchor component={Link} to="/loaded-models" size="sm" onClick={() => setOpened(false)}>
						{t("runtimeResources.openLoadedModels", "Manage loaded models")}
					</Anchor>
				</Stack>
			</Popover.Dropdown>
		</Popover>
	);
}
