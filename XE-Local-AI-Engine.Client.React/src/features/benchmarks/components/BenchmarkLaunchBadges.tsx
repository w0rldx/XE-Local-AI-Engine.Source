import { Group, type MantineColor, Text, Tooltip } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import type { BenchmarkLaunchFacts } from "@/features/benchmarks/models/BenchmarkModels";

interface BenchmarkLaunchBadgesProps {
	launch: BenchmarkLaunchFacts;
	"data-testid"?: string;
}

interface BackendBadge {
	label: string;
	color: MantineColor;
}

// Facts, never a verdict: the row states which KV cache type was used, whether flash attention was on, where the
// layers actually landed, and whether an aux asset was attached. Runs frozen before the receipt existed show "—".
export function BenchmarkLaunchBadges({ launch, "data-testid": testId }: BenchmarkLaunchBadgesProps) {
	const { t } = useTranslation();

	const backend = (): BackendBadge | null => {
		const value = launch.effectiveBackend;
		if (value === null) {
			return null;
		}
		if (value === "cpu-fallback") {
			return { label: t("pages.benchmarks.launch.cpuFallback", "CPU fallback"), color: "orange" };
		}
		if (value === "unknown") {
			return { label: t("pages.benchmarks.launch.backendUnknown", "backend unknown"), color: "gray" };
		}
		if (value === "metal-unverified") {
			return { label: t("pages.benchmarks.launch.metalUnverified", "Metal (unverified)"), color: "gray" };
		}
		if (value === "cpu") {
			return { label: t("pages.benchmarks.launch.cpu", "CPU"), color: "gray" };
		}
		const name = value.toUpperCase();
		return {
			label:
				launch.placementTotal === null
					? name
					: t("pages.benchmarks.launch.layers", "{{backend}} {{offloaded}}/{{total}} layers", {
							backend: name,
							offloaded: launch.placementOffloaded ?? 0,
							total: launch.placementTotal,
						}),
			color: "blue",
		};
	};

	const backendBadge = backend();
	// A null source is "not recorded" (D7), which must not be dressed up as an explicit operator pick: the suffix
	// appears only for a source the node actually wrote.
	const kvLabel =
		launch.kvCacheType === null
			? null
			: launch.kvCacheTypeSource === "auto"
				? t("pages.benchmarks.launch.kvAuto", "KV {{type}} (auto)", { type: launch.kvCacheType })
				: launch.kvCacheTypeSource === "explicit"
					? t("pages.benchmarks.launch.kvExplicit", "KV {{type}} (explicit)", { type: launch.kvCacheType })
					: t("pages.benchmarks.launch.kv", "KV {{type}}", { type: launch.kvCacheType });

	if (kvLabel === null && launch.flashAttentionMode === null && backendBadge === null && launch.hasAuxAssets !== true) {
		return (
			<Text size="xs" c="dimmed" data-testid={testId}>
				{t("pages.benchmarks.launch.none", "—")}
			</Text>
		);
	}

	return (
		<Group gap="xs" data-testid={testId}>
			{kvLabel === null ? null : (
				<Tooltip label={launch.kvAutoReason} disabled={launch.kvAutoReason === null}>
					<span>
						<StatusBadge color="grape" label={kvLabel} data-testid="benchmark-launch-kv" />
					</span>
				</Tooltip>
			)}
			{launch.flashAttentionMode === null ? null : (
				<StatusBadge
					color="grape"
					label={t("pages.benchmarks.launch.flashAttention", "FA {{mode}}", { mode: launch.flashAttentionMode })}
					data-testid="benchmark-launch-fa"
				/>
			)}
			{backendBadge === null ? null : (
				<StatusBadge color={backendBadge.color} label={backendBadge.label} data-testid="benchmark-launch-backend" />
			)}
			{launch.hasAuxAssets === true ? (
				<StatusBadge
					color="yellow"
					label={t("pages.benchmarks.launch.auxAsset", "adapter/aux asset")}
					data-testid="benchmark-launch-aux"
				/>
			) : null}
		</Group>
	);
}
