import { Alert, CloseButton, Group, Text } from "@mantine/core";
import { IconCpu } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { useHardwareProfile } from "@/features/model-fit/queries/useModelFit";
import { useCpuFallbackBannerStore } from "@/features/model-fit/stores/CpuFallbackBannerStore";

// Global "no supported GPU detected — running on CPU" banner, mounted once in the app shell next to the runtime-update
// banner (its mount is gated on the model-fit capability, which owns the hardware-profile endpoint). Renders only when
// the backend reports GPU acceleration is unavailable AND the operator has not dismissed the notice. `gpuAccelAvailable`
// is authoritative: the backend sets it true only when VRAM is known for a recognised NVIDIA/AMD/Intel adapter, so
// false reliably means inference will run on CPU — including AMD/Intel Windows boxes whose VRAM probe is still a stub.
export function CpuFallbackBanner() {
	const { t } = useTranslation();
	const hardwareQuery = useHardwareProfile();
	const dismissed = useCpuFallbackBannerStore((state) => state.dismissed);
	const dismiss = useCpuFallbackBannerStore((state) => state.dismiss);

	const profile = hardwareQuery.data;
	const shouldShow = profile?.gpuAccelAvailable === false && !dismissed;

	if (!shouldShow) {
		return null;
	}

	return (
		<Alert
			color="orange"
			variant="light"
			icon={<IconCpu size={18} />}
			radius={0}
			withCloseButton={false}
			data-testid="cpu-fallback-banner"
		>
			<Group justify="space-between" align="center" wrap="nowrap" gap="sm">
				<Text size="sm">
					{t(
						"pages.modelFit.hardware.cpuFallback.message",
						"No supported GPU detected — running on CPU. Responses will be slower.",
					)}
				</Text>
				<CloseButton
					aria-label={t("pages.modelFit.hardware.cpuFallback.dismiss", "Dismiss")}
					onClick={dismiss}
					data-testid="cpu-fallback-banner-dismiss"
				/>
			</Group>
		</Alert>
	);
}
