import { Stack, Text, Tooltip } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import {
	formatKldValue,
	formatPerplexity,
	formatTopTokenAgreement,
	isKldComparable,
} from "@/features/benchmarks/models/BenchmarkFidelity";
import type { BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";

/**
 * How far this build drifted from the weights it was made from. Perplexity leads because it needs no second model;
 * KLD rides under it when the project opted in. Display only, and the copy never calls a number good or bad — a lower
 * perplexity is not a better answer, which is exactly why neither figure ranks anything.
 *
 * A KLD measured against something the project no longer expects renders `kld-stale` — a BADGE, never a greyed number.
 * A figure a reader can still see is a figure they will compare, and one taken over a different corpus, chunk count or
 * base model means something different from the one beside it.
 */
export function FidelityCell({ run }: { run: BenchmarkRunSummary }) {
	const { t } = useTranslation();
	const fidelity = run.fidelity;
	if (fidelity === null || fidelity.status === "skipped") {
		return <Text size="sm">—</Text>;
	}
	if (fidelity.status === "queued" || fidelity.status === "running") {
		return (
			<StatusBadge
				color="blue"
				inProgress={true}
				label={t(`pages.benchmarks.fidelity.status.${fidelity.status}`, fidelity.status)}
				data-testid={`benchmark-fidelity-status-${run.id}`}
			/>
		);
	}
	if (fidelity.status === "failed" || fidelity.status === "cancelled") {
		return (
			<Tooltip
				label={fidelity.errorMessage ?? t("pages.benchmarks.fidelity.noReason", "The node recorded no reason.")}
				multiline={true}
				w={280}
			>
				<span>
					<StatusBadge
						color={fidelity.status === "failed" ? "red" : "gray"}
						label={t(`pages.benchmarks.fidelity.status.${fidelity.status}`, fidelity.status)}
						data-testid={`benchmark-fidelity-status-${run.id}`}
					/>
				</span>
			</Tooltip>
		);
	}
	const perplexity = formatPerplexity(fidelity);
	const comparable = isKldComparable(fidelity);
	const tooltip = [
		perplexity === null
			? null
			: t(
					"pages.benchmarks.fidelity.pplTooltip",
					"Perplexity {{value}} over {{chunks}} chunks at a {{window}}-token window ({{corpus}})",
					{
						value: perplexity,
						chunks: fidelity.perplexityChunks ?? "—",
						window: fidelity.perplexityContextTokens ?? "—",
						corpus: fidelity.perplexityCorpusId ?? "—",
					},
				),
		comparable && fidelity.kldMean !== null
			? t("pages.benchmarks.fidelity.kldTooltip", "KL divergence mean {{mean}}, p99 {{p99}}, top-token agreement {{agreement}}", {
					mean: formatKldValue(fidelity.kldMean) ?? "—",
					p99: formatKldValue(fidelity.kldP99) ?? "—",
					agreement: formatTopTokenAgreement(fidelity.topTokenAgreement) ?? "—",
				})
			: null,
		t("pages.benchmarks.fidelity.displayOnly", "Display only — fidelity never ranks a run."),
	]
		.filter((line): line is string => line !== null)
		.join("\n");
	return (
		<Tooltip label={tooltip} multiline={true} w={320}>
			<Stack gap={0} data-testid={`benchmark-fidelity-${run.id}`}>
				<Text size="sm">{perplexity ?? "—"}</Text>
				{fidelity.kldState === "kld-stale" ? (
					<StatusBadge
						color="orange"
						label={t("pages.benchmarks.fidelity.kldStale", "kld-stale")}
						data-testid={`benchmark-fidelity-kld-stale-${run.id}`}
					/>
				) : comparable && fidelity.kldMean !== null ? (
					<Text size="xs" c="dimmed" data-testid={`benchmark-fidelity-kld-${run.id}`}>
						{t("pages.benchmarks.fidelity.kldLine", "KLD {{mean}} · p99 {{p99}} · {{agreement}}", {
							mean: formatKldValue(fidelity.kldMean),
							p99: formatKldValue(fidelity.kldP99) ?? "—",
							agreement: formatTopTokenAgreement(fidelity.topTokenAgreement) ?? "—",
						})}
					</Text>
				) : null}
			</Stack>
		</Tooltip>
	);
}
