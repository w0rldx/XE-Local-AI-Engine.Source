import { Alert, Badge, Button, Group, Loader, Table, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconBolt, IconInfoCircle, IconSnowflake, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { ProfileMetricsCard } from "@/features/model-fit/components/ProfileMetricsCard";
import {
	formatProfileOutcomeSummary,
	type InferenceBenchmarkResult,
	type InferenceProfileStatus,
	type InferenceProfileView,
} from "@/features/model-fit/models/InferenceProfileModels";

const statusColor: Record<InferenceProfileStatus, string> = {
	explored: "blue",
	frozen: "green",
	stale: "gray",
};

interface InferenceProfileListProps {
	readonly profiles: readonly InferenceProfileView[];
	readonly benchmarkResults: Readonly<Record<string, InferenceBenchmarkResult>>;
	readonly loadState: { readonly loading: boolean; readonly error: unknown };
	readonly pending: {
		readonly benchmarkProfileId?: string;
		readonly freezeProfileId?: string;
		readonly invalidateProfileId?: string;
	};
	readonly onBenchmark: (profile: InferenceProfileView) => void;
	readonly onFreeze: (profile: InferenceProfileView) => void;
	readonly onInvalidate: (profile: InferenceProfileView) => void;
}

export function InferenceProfileList({
	profiles,
	benchmarkResults,
	loadState,
	pending,
	onBenchmark,
	onFreeze,
	onInvalidate,
}: InferenceProfileListProps) {
	const { t } = useTranslation();

	if (loadState.loading) {
		return (
			<Group gap="sm">
				<Loader size="sm" />
				<Text c="dimmed">{t("pages.modelFit.inferenceProfiles.loading", "Loading inference profiles…")}</Text>
			</Group>
		);
	}

	if (loadState.error) {
		return (
			<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="inference-profile-error">
				{apiErrorMessage(loadState.error, t("pages.modelFit.inferenceProfiles.error", "Could not load inference profiles."))}
			</Alert>
		);
	}

	if (profiles.length === 0) {
		return (
			<Alert color="gray" icon={<IconInfoCircle size={16} />} data-testid="inference-profile-empty">
				{t("pages.modelFit.inferenceProfiles.empty", "No inference profiles yet. Explore a model to create one.")}
			</Alert>
		);
	}

	return (
		<Table.ScrollContainer minWidth={720}>
			<Table verticalSpacing="sm" highlightOnHover={true} data-testid="inference-profile-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.modelFit.inferenceProfiles.columns.status", "Status")}</Table.Th>
						<Table.Th>{t("pages.modelFit.inferenceProfiles.columns.model", "Model")}</Table.Th>
						<Table.Th>{t("pages.modelFit.inferenceProfiles.columns.outcome", "Outcome")}</Table.Th>
						<Table.Th>{t("pages.modelFit.inferenceProfiles.columns.action", "Action")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{profiles.map((profile) => (
						<InferenceProfileRow
							key={profile.id}
							profile={profile}
							result={benchmarkResults[profile.id]}
							isBenchmarking={pending.benchmarkProfileId === profile.id}
							isFreezing={pending.freezeProfileId === profile.id}
							isInvalidating={pending.invalidateProfileId === profile.id}
							onBenchmark={onBenchmark}
							onFreeze={onFreeze}
							onInvalidate={onInvalidate}
						/>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}

interface InferenceProfileRowProps {
	readonly profile: InferenceProfileView;
	readonly result?: InferenceBenchmarkResult;
	readonly isBenchmarking: boolean;
	readonly isFreezing: boolean;
	readonly isInvalidating: boolean;
	readonly onBenchmark: (profile: InferenceProfileView) => void;
	readonly onFreeze: (profile: InferenceProfileView) => void;
	readonly onInvalidate: (profile: InferenceProfileView) => void;
}

function InferenceProfileRow({
	profile,
	result,
	isBenchmarking,
	isFreezing,
	isInvalidating,
	onBenchmark,
	onFreeze,
	onInvalidate,
}: InferenceProfileRowProps) {
	const { t } = useTranslation();
	const tokensPerSecond = result?.metrics?.tokensPerSecond ?? null;
	const vramBytes = profile.frozenGlobalFreeVramBytes ?? result?.metrics?.globalFreeVramAfterBytes ?? null;
	const summary = formatProfileOutcomeSummary(tokensPerSecond, vramBytes);
	const canFreeze = profile.hasBenchmark || result !== undefined;
	const freezeButton = (
		<Button
			size="xs"
			variant="light"
			color="cyan"
			leftSection={<IconSnowflake size={14} />}
			loading={isFreezing}
			disabled={!canFreeze}
			onClick={() => onFreeze(profile)}
			data-testid={`inference-profile-freeze-${profile.id}`}
		>
			{t("pages.modelFit.inferenceProfiles.actions.freeze", "Freeze")}
		</Button>
	);

	return (
		<>
			<Table.Tr data-testid={`inference-profile-row-${profile.id}`}>
				<Table.Td>
					<Tooltip
						label={t(
							"pages.modelFit.inferenceProfiles.status.staleTooltip",
							"Re-exploration required. A profile goes stale when the llama.cpp build, the GPU, the free VRAM at freeze, or this node's KV cache type has changed since it was frozen.",
						)}
						multiline={true}
						w={320}
						withArrow={true}
						disabled={profile.status !== "stale"}
					>
						<Badge color={statusColor[profile.status]} variant="light" data-testid={`inference-profile-status-${profile.id}`}>
							{t(`pages.modelFit.inferenceProfiles.status.${profile.status}`, profile.status)}
						</Badge>
					</Tooltip>
				</Table.Td>
				<Table.Td>
					<Text size="sm" fw={500}>
						{profile.modelName}
					</Text>
					<Group gap={6} mt={2}>
						{profile.backend ? (
							<Text size="xs" c="dimmed">
								{profile.backend}
							</Text>
						) : null}
						{profile.quant ? (
							<Text size="xs" c="dimmed">
								{profile.quant}
							</Text>
						) : null}
						{profile.launchPolicyFingerprint && profile.launchPolicyFingerprintVersion !== null ? (
							<Text size="xs" c="dimmed" data-testid={`inference-profile-fingerprint-${profile.id}`}>
								{t("pages.modelFit.inferenceProfiles.policyFingerprint", "Policy v{{version}} · {{fingerprint}}", {
									version: profile.launchPolicyFingerprintVersion,
									fingerprint: profile.launchPolicyFingerprint.slice(0, 8),
								})}
							</Text>
						) : null}
						{profile.frozenGlobalFreeVramBytes !== null || profile.frozenProcessBudgetVramBytes !== null ? (
							<Text size="xs" c="dimmed" data-testid={`inference-profile-freeze-vram-${profile.id}`}>
								{t(
									"pages.modelFit.inferenceProfiles.freezeVramSummary",
									"Global free {{globalFree}} · process budget {{processBudget}}",
									{
										globalFree:
											profile.frozenGlobalFreeVramBytes === null
												? t("pages.modelFit.inferenceProfiles.metrics.unknown", "Unknown")
												: `${(profile.frozenGlobalFreeVramBytes / 1024 ** 3).toFixed(1)} GB`,
										processBudget:
											profile.frozenProcessBudgetVramBytes === null
												? t("pages.modelFit.inferenceProfiles.metrics.unknown", "Unknown")
												: `${(profile.frozenProcessBudgetVramBytes / 1024 ** 3).toFixed(1)} GB`,
									},
								)}
							</Text>
						) : null}
						{profile.isMoe ? (
							<Badge size="xs" variant="outline" color="grape" data-testid={`inference-profile-moe-${profile.id}`}>
								{profile.expertCount !== null
									? t("pages.modelFit.inferenceProfiles.moeExperts", "MoE · {{count}} experts", { count: profile.expertCount })
									: t("pages.modelFit.inferenceProfiles.moe", "MoE")}
							</Badge>
						) : null}
					</Group>
				</Table.Td>
				<Table.Td>
					<Text size="sm" c={summary ? undefined : "dimmed"} data-testid={`inference-profile-outcome-${profile.id}`}>
						{summary ?? "—"}
					</Text>
				</Table.Td>
				<Table.Td>
					<Group gap="xs" wrap="nowrap">
						{profile.status !== "frozen" ? (
							<>
								<Button
									size="xs"
									variant="light"
									leftSection={<IconBolt size={14} />}
									loading={isBenchmarking}
									onClick={() => onBenchmark(profile)}
									data-testid={`inference-profile-benchmark-${profile.id}`}
								>
									{t("pages.modelFit.inferenceProfiles.actions.benchmark", "Benchmark")}
								</Button>
								{canFreeze ? (
									freezeButton
								) : (
									<Tooltip
										label={t(
											"pages.modelFit.inferenceProfiles.actions.freezeDisabledHint",
											"Run a benchmark before freezing this profile.",
										)}
										multiline={true}
										maw={220}
									>
										<span>{freezeButton}</span>
									</Tooltip>
								)}
							</>
						) : (
							<Button
								size="xs"
								variant="light"
								color="red"
								leftSection={<IconTrash size={14} />}
								loading={isInvalidating}
								onClick={() => onInvalidate(profile)}
								data-testid={`inference-profile-invalidate-${profile.id}`}
							>
								{t("pages.modelFit.inferenceProfiles.actions.invalidate", "Invalidate")}
							</Button>
						)}
					</Group>
				</Table.Td>
			</Table.Tr>
			{result?.metrics ? (
				<Table.Tr data-testid={`inference-profile-metrics-row-${profile.id}`}>
					<Table.Td colSpan={4}>
						<ProfileMetricsCard metrics={result.metrics} testIdSuffix={profile.id} />
					</Table.Td>
				</Table.Tr>
			) : null}
		</>
	);
}
