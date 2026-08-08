import { Alert, Badge, Button, Card, Group, Loader, SimpleGrid, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCpu, IconHelpCircle, IconRefresh } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { formatBytesAsGb } from "@/core/formatting/BytesFormatting";
import type { HardwareProfile } from "@/features/model-fit/models/ModelFitModels";

interface HardwareProfileCardProps {
	profile: HardwareProfile | undefined;
	isLoading: boolean;
	isFetching: boolean;
	error: unknown;
	onRefresh: () => void;
}

function Stat({ label, value, testId }: { label: string; value: string; testId: string }) {
	return (
		<Stack gap={0}>
			<Text size="xs" c="dimmed">
				{label}
			</Text>
			<Text size="sm" fw={500} data-testid={testId}>
				{value}
			</Text>
		</Stack>
	);
}

// Narrows the measured layer-placement fields to the case where BOTH counts are present and self-consistent. The
// backend sends them together or not at all, but the wire type makes each independently nullable, so the guard keeps a
// half-populated payload from rendering "38 / null on GPU".
function layerPlacement(profile: HardwareProfile): { offloaded: number; total: number; isPartial: boolean } | null {
	const { gpuOffloadedLayers: offloaded, gpuTotalLayers: total } = profile;
	if (offloaded === null || total === null || total <= 0 || offloaded < 0 || offloaded > total) {
		return null;
	}

	return { offloaded, total, isPartial: offloaded < total };
}

// Hardware-profile summary card: RAM / VRAM (or "VRAM unknown") / GPU vendor / CPU cores / disk / measured GPU layer
// placement, plus a CPU-mode badge when GPU acceleration is unavailable and a "refresh hardware" action that re-probes
// the box. Three runtime-truth states are surfaced separately and never conflated, because each calls for a different
// response: a total CPU fallback (the GPU is not being used at all), a PARTIAL offload (the GPU is being used, but part
// of the model runs from system RAM), and an undetermined backend (the probe could not answer, so neither claim is
// safe). Server state is owned by the page's useHardwareProfile query; this component is pure presentation.
export function HardwareProfileCard({ profile, isLoading, isFetching, error, onRefresh }: HardwareProfileCardProps) {
	const { t } = useTranslation();
	const placement = profile ? layerPlacement(profile) : null;
	const showRuntimeAlerts = Boolean(profile) && !isLoading && !error;

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="model-fit-hardware-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Group gap="xs" align="center">
						<IconCpu size={20} />
						<Title order={4}>{t("pages.modelFit.hardware.title", "Hardware profile")}</Title>
						{profile && !profile.gpuAccelAvailable ? (
							<Badge color="orange" variant="light" data-testid="model-fit-hardware-cpu-mode-badge">
								{t("pages.modelFit.hardware.cpuMode", "CPU mode")}
							</Badge>
						) : null}
					</Group>
					<Button
						variant="default"
						size="xs"
						leftSection={<IconRefresh size={14} />}
						loading={isFetching}
						onClick={onRefresh}
						data-testid="model-fit-hardware-refresh"
					>
						{t("pages.modelFit.hardware.refresh", "Refresh hardware")}
					</Button>
				</Group>

				{isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.modelFit.hardware.loading", "Detecting hardware…")}</Text>
					</Group>
				) : null}

				{error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="model-fit-hardware-error">
						{apiErrorMessage(error, t("pages.modelFit.hardware.error", "Could not detect hardware."))}
					</Alert>
				) : null}

				{showRuntimeAlerts && profile?.cpuFallback ? (
					<Alert
						color="orange"
						variant="light"
						icon={<IconAlertTriangle size={16} />}
						title={t("pages.modelFit.hardware.cpuFallbackAlert.title", "Running on CPU despite a detected GPU")}
						data-testid="model-fit-hardware-cpu-fallback-alert"
					>
						<Stack gap={4}>
							<Text size="sm">
								{profile.cpuFallbackReason ??
									t(
										"pages.modelFit.hardware.cpuFallbackAlert.reason",
										"The selected inference runtime could not use this machine's GPU, so inference is running on the CPU.",
									)}
							</Text>
							{profile.cpuFallbackRemediation ? (
								<Text size="sm" c="dimmed" data-testid="model-fit-hardware-cpu-fallback-remediation">
									{profile.cpuFallbackRemediation}
								</Text>
							) : null}
						</Stack>
					</Alert>
				) : null}

				{showRuntimeAlerts && profile?.backendUndeterminedReason ? (
					<Alert
						color="yellow"
						variant="light"
						icon={<IconHelpCircle size={16} />}
						title={t("pages.modelFit.hardware.backendUndeterminedAlert.title", "Could not determine your GPU backend")}
						data-testid="model-fit-hardware-backend-undetermined-alert"
					>
						<Text size="sm">{profile.backendUndeterminedReason}</Text>
					</Alert>
				) : null}

				{showRuntimeAlerts && placement?.isPartial ? (
					<Alert
						color="orange"
						variant="light"
						icon={<IconAlertTriangle size={16} />}
						title={t("pages.modelFit.hardware.partialOffloadAlert.title", "Part of this model is running on the CPU")}
						data-testid="model-fit-hardware-partial-offload-alert"
					>
						<Stack gap={4}>
							<Text size="sm">
								{t("pages.modelFit.hardware.partialOffloadAlert.reason", {
									defaultValue:
										"Only {{offloaded}} of {{model}}'s {{total}} layers fit on the GPU. The remaining {{remaining}} run from system RAM, which is substantially slower — the model still answers correctly, just at a fraction of the speed.",
									offloaded: placement.offloaded,
									total: placement.total,
									remaining: placement.total - placement.offloaded,
									model: profile?.gpuOffloadModelName ?? t("pages.modelFit.hardware.thisModel", "this model"),
								})}
							</Text>
							<Text size="sm" c="dimmed" data-testid="model-fit-hardware-partial-offload-remediation">
								{t(
									"pages.modelFit.hardware.partialOffloadAlert.remediation",
									"A smaller quantization, a shorter context, or freeing VRAM used by other processes will usually fit the whole model.",
								)}
							</Text>
						</Stack>
					</Alert>
				) : null}

				{profile && !isLoading && !error ? (
					<SimpleGrid cols={{ base: 2, sm: 3, md: 5 }} spacing="lg">
						<Stat
							label={t("pages.modelFit.hardware.totalRam", "Total RAM")}
							value={formatBytesAsGb(profile.totalRamBytes)}
							testId="model-fit-hardware-total-ram"
						/>
						<Stat
							label={t("pages.modelFit.hardware.availableRam", "Available RAM")}
							value={formatBytesAsGb(profile.availableRamBytes)}
							testId="model-fit-hardware-available-ram"
						/>
						<Stat
							label={t("pages.modelFit.hardware.vram", "VRAM")}
							value={
								profile.vramKnown
									? formatBytesAsGb(profile.vramBytes)
									: t("pages.modelFit.hardware.vramUnknown", "VRAM unknown")
							}
							testId="model-fit-hardware-vram"
						/>
						<Stat
							label={t("pages.modelFit.hardware.gpuVendor", "GPU vendor")}
							value={t(`pages.modelFit.hardware.vendors.${profile.gpuVendor}`, profile.gpuVendor)}
							testId="model-fit-hardware-gpu-vendor"
						/>
						<Stat
							label={t("pages.modelFit.hardware.cpuCores", "CPU cores")}
							value={String(profile.cpuCores)}
							testId="model-fit-hardware-cpu-cores"
						/>
						<Stat
							label={t("pages.modelFit.hardware.freeDisk", "Free disk")}
							value={formatBytesAsGb(profile.freeDiskBytes)}
							testId="model-fit-hardware-free-disk"
						/>
						{/* Measured, not inferred: what the last observed model load actually did with that model's layers.
						    "Not measured yet" is the honest reading before any model has loaded — it is not a claim of zero. */}
						<Stat
							label={t("pages.modelFit.hardware.gpuLayers", "Layers on GPU")}
							value={
								placement
									? `${placement.offloaded} / ${placement.total}`
									: t("pages.modelFit.hardware.gpuLayersUnknown", "Not measured yet")
							}
							testId="model-fit-hardware-gpu-layers"
						/>
					</SimpleGrid>
				) : null}
			</Stack>
		</Card>
	);
}
