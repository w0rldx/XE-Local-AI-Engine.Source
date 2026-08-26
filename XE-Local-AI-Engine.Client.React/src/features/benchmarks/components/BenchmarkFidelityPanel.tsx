import { Alert, Button, Code, Group, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconChevronDown, IconChevronRight, IconRuler2, IconTrash } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { formatBytesAsGb } from "@/core/formatting/BytesFormatting";
import { toast } from "@/core/ui/notifications/Toast";
import type { BenchmarkProjectFidelity } from "@/features/benchmarks/models/BenchmarkModels";
import { useBenchmarkKldDiskEstimate, useClearBenchmarkFidelityCache } from "@/features/benchmarks/queries/useBenchmarks";

/**
 * The disk side of the quant-fidelity axis. Perplexity costs nothing but a pass over a shipped corpus; KL divergence
 * needs a cached logit file for the BASE model, which is ~1.75 bytes per logit — 200 chunks of a 150k-vocabulary model
 * is 25 GB. So the estimate is shown BEFORE the operator commits to it and never discovered afterwards (plan §2 #3),
 * and it is read only while this panel is open, because the answer moves whenever the disk does.
 *
 * follow-up: the KLD opt-in toggle and the base-model selector belong here, but `BenchmarkProjectMutationRequest` /
 * `BenchmarkProjectDetailResponse` do not carry `fidelityEnabled`, `fidelityKldEnabled`, `fidelityChunks` or
 * `fidelityKldBaseModelName` yet — the columns exist on `BenchmarkProject` but were never projected onto the endpoint
 * DTOs in S1. Both controls go in unchanged the moment those four members reach the generated client; until then this
 * panel reports the cost and manages the cache, and the measurement itself is triggered per run from the runs table.
 */
export function BenchmarkFidelityPanel({ projectId, fidelity }: { projectId: string; fidelity: BenchmarkProjectFidelity }) {
	const { t } = useTranslation();
	const [opened, setOpened] = useState(false);
	// Asked for while the section is open whether or not KLD is on: the estimate is exactly what the operator needs
	// BEFORE deciding to enable it, so gating it on the setting would hide the number that informs the setting.
	const estimateQuery = useBenchmarkKldDiskEstimate(projectId, fidelity.chunks ?? undefined, opened);
	const clearCache = useClearBenchmarkFidelityCache();
	const estimate = estimateQuery.data;

	return (
		<Stack gap="xs" data-testid="benchmark-fidelity-panel">
			<Group gap="xs">
				<Button
					variant="subtle"
					size="xs"
					leftSection={opened ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
					onClick={() => setOpened((current) => !current)}
					aria-expanded={opened}
					data-testid="benchmark-fidelity-toggle"
				>
					{t("pages.benchmarks.fidelity.title", "Quant fidelity (PPL / KLD)")}
				</Button>
				<StatusBadge
					color={fidelity.enabled ? "blue" : "gray"}
					label={
						fidelity.enabled
							? fidelity.kldEnabled
								? t("pages.benchmarks.fidelity.onWithKld", "PPL + KLD")
								: t("pages.benchmarks.fidelity.onPplOnly", "PPL")
							: t("pages.benchmarks.fidelity.off", "off")
					}
					data-testid="benchmark-fidelity-state"
				/>
			</Group>
			{opened ? (
				<Stack gap="xs">
					<Text size="xs" c="dimmed">
						{t(
							"pages.benchmarks.fidelity.explanation",
							"Perplexity and KL divergence measure how far a quantized build drifted from the weights it was made from. Both are display only and neither ever ranks a run.",
						)}
					</Text>
					<Text size="xs" c="dimmed" data-testid="benchmark-fidelity-settings">
						{fidelity.enabled
							? t("pages.benchmarks.fidelity.settings", "Scoring {{chunks}} chunks{{base}}.", {
									chunks: fidelity.chunksEffective,
									base: fidelity.kldBaseModelName === null ? "" : `, KLD against ${fidelity.kldBaseModelName}`,
								})
							: t("pages.benchmarks.fidelity.disabled", "Not measured. Enable it in the project settings before the first run.")}
					</Text>
					{estimateQuery.isLoading ? (
						<Group gap="sm">
							<Loader size="xs" />
							<Text size="xs" c="dimmed">
								{t("pages.benchmarks.fidelity.estimateLoading", "Estimating the base-logit cache…")}
							</Text>
						</Group>
					) : null}
					{estimateQuery.error ? (
						<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="benchmark-fidelity-estimate-error">
							{apiErrorMessage(
								estimateQuery.error,
								t("pages.benchmarks.fidelity.estimateError", "Could not estimate the KL-divergence cache size."),
							)}
						</Alert>
					) : null}
					{estimate ? (
						<Stack gap={4} data-testid="benchmark-kld-estimate">
							<Text size="sm">
								{t(
									"pages.benchmarks.fidelity.estimate",
									"KL divergence would cache {{size}} of base logits — {{free}} free, {{cached}} already cached.",
									{
										size: formatBytesAsGb(estimate.estimatedBytes),
										free: formatBytesAsGb(estimate.freeDiskBytes),
										cached: formatBytesAsGb(estimate.cachedBytes),
									},
								)}
							</Text>
							{/* The arithmetic, verbatim from the node, so the number is checkable rather than trusted. */}
							<Code data-testid="benchmark-kld-estimate-formula">{estimate.formula}</Code>
							<Text size="xs" c="dimmed">
								{t("pages.benchmarks.fidelity.estimateInputs", "{{chunks}} chunks × {{window}} tokens × {{vocab}} vocabulary", {
									chunks: estimate.chunks,
									window: estimate.contextTokens,
									vocab: estimate.vocabSize,
								})}
							</Text>
							{estimate.fitsOnDisk ? null : (
								<Alert color="orange" icon={<IconAlertTriangle size={16} />} data-testid="benchmark-kld-estimate-too-large">
									{t(
										"pages.benchmarks.fidelity.estimateTooLarge",
										"This does not fit on the disk the node writes to. The measurement would be refused rather than half-written — free space or lower the chunk count first.",
									)}
								</Alert>
							)}
						</Stack>
					) : null}
					<Group gap="xs">
						<Button
							variant="default"
							size="xs"
							leftSection={<IconTrash size={14} />}
							loading={clearCache.isPending}
							onClick={() =>
								clearCache.mutate(projectId, {
									onSuccess: () => {
										toast.success(t("pages.benchmarks.fidelity.cacheCleared", "Cached base logits deleted."));
										estimateQuery.refetch();
									},
									onError: (error) =>
										toast.error(
											apiErrorMessage(
												error,
												t("pages.benchmarks.fidelity.cacheClearError", "Could not delete the cached base logits."),
											),
										),
								})
							}
							data-testid="benchmark-fidelity-clear-cache"
						>
							{t("pages.benchmarks.fidelity.clearCache", "Delete cached base logits")}
						</Button>
						<Text size="xs" c="dimmed">
							<IconRuler2 size={12} style={{ verticalAlign: "middle" }} />{" "}
							{t(
								"pages.benchmarks.fidelity.clearCacheHint",
								"Nothing measured is lost — the runs keep their numbers. The next KL-divergence pass recomputes the base logits.",
							)}
						</Text>
					</Group>
				</Stack>
			) : null}
		</Stack>
	);
}
