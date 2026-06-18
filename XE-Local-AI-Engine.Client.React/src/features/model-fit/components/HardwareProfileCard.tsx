import { Alert, Badge, Button, Card, Group, Loader, SimpleGrid, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCpu, IconRefresh } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { formatBytesAsGb } from "@/features/model-fit/components/ModelFitFormatters";
import type { HardwareProfile } from "@/features/model-fit/models/ModelFitModels";

interface HardwareProfileCardProps {
	profile: HardwareProfile | undefined;
	isLoading: boolean;
	isFetching: boolean;
	error: unknown;
	onRefresh: () => void;
}

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
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

// Hardware-profile summary card: RAM / VRAM (or "VRAM unknown") / GPU vendor / CPU cores / disk, plus a CPU-mode
// badge when GPU acceleration is unavailable and a "refresh hardware" action that re-probes the box. Server state is
// owned by the page's useHardwareProfile query; this component is pure presentation over the resolved profile.
export function HardwareProfileCard({ profile, isLoading, isFetching, error, onRefresh }: HardwareProfileCardProps) {
	const { t } = useTranslation();

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
						{errorMessage(error, t("pages.modelFit.hardware.error", "Could not detect hardware."))}
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
					</SimpleGrid>
				) : null}
			</Stack>
		</Card>
	);
}
