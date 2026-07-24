import { Alert, Badge, Button, Card, Group, Loader, Select, Stack, Table, Text, TextInput, Title, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconBolt, IconInfoCircle, IconPlayerPlay, IconSettings, IconSnowflake, IconTrash } from "@tabler/icons-react";
import { Fragment, useState } from "react";
import { useTranslation } from "react-i18next";

import { toast } from "@/core/ui/notifications/Toast";
import { ProfileMetricsCard } from "@/features/model-fit/components/ProfileMetricsCard";
import {
	formatProfileOutcomeSummary,
	type InferenceBenchmarkResult,
	type InferenceProfileStatus,
	type InferenceProfileView,
} from "@/features/model-fit/models/InferenceProfileModels";
import {
	useBenchmarkInferenceProfile,
	useExploreInferenceProfile,
	useFreezeInferenceProfile,
	useInferenceProfiles,
	useInvalidateInferenceProfile,
} from "@/features/model-fit/queries/useInferenceProfiles";

// Badge color per terminal status: a frozen (committed) profile is the desirable outcome (green); an explored
// candidate is in-progress (blue); a stale one needs re-exploration (gray).
const statusColor: Record<InferenceProfileStatus, string> = {
	explored: "blue",
	frozen: "green",
	stale: "gray",
};

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

// The Inference Optimizer operator surface. Lists the tuned llama.cpp launch profiles this node explored,
// benchmarked, and froze, plus an explore control to create a new one. It surfaces OUTCOMES ONLY — a status chip and
// the tok/s + VRAM summary — and never the raw launch flags the optimizer tuned (the view-model has no slot for
// them, and there is no machine key on the wire). Server state lives in TanStack Query; the only local state is the
// explore form and the ephemeral in-session benchmark results used to enrich a row and render its metrics card.
export function InferenceProfilePanel() {
	const { t } = useTranslation();

	const profilesQuery = useInferenceProfiles();
	const explore = useExploreInferenceProfile();
	const benchmark = useBenchmarkInferenceProfile();
	const freeze = useFreezeInferenceProfile();
	const invalidate = useInvalidateInferenceProfile();

	const [modelName, setModelName] = useState("");
	// `role` is the llama-server ModelRole on the wire — only "chat" or "embedding" (the backend rejects anything else).
	const [role, setRole] = useState<string>("chat");
	// Ephemeral, per-row benchmark outcomes keyed by profile id — enriches the outcome line + renders the metrics card.
	const [benchmarkResults, setBenchmarkResults] = useState<Record<string, InferenceBenchmarkResult>>({});

	const profiles = profilesQuery.data ?? [];

	// Only the two llama-server roles are valid explore targets (chat vs embedding); the backend parses exactly these.
	const roleData = [
		{ value: "chat", label: t("pages.modelFit.inferenceProfiles.explore.roleChat", "Chat") },
		{ value: "embedding", label: t("pages.modelFit.inferenceProfiles.explore.roleEmbedding", "Embedding") },
	];

	const handleExplore = (): void => {
		const trimmed = modelName.trim();
		if (trimmed.length === 0) {
			return;
		}
		explore.mutate(
			{ modelName: trimmed, role },
			{
				onSuccess: () => {
					toast.success(
						t("pages.modelFit.inferenceProfiles.explore.success", "Exploring a tuned profile for {{model}}.", { model: trimmed }),
					);
					setModelName("");
				},
				onError: (error) =>
					toast.error(errorMessage(error, t("pages.modelFit.inferenceProfiles.explore.error", "Could not start exploration."))),
			},
		);
	};

	const handleBenchmark = (profile: InferenceProfileView): void => {
		benchmark.mutate(
			{ profileId: profile.id },
			{
				onSuccess: (result) => {
					setBenchmarkResults((previous) => ({ ...previous, [profile.id]: result }));
					toast.success(t("pages.modelFit.inferenceProfiles.actions.benchmarkSuccess", "Benchmarked {{model}}.", { model: profile.modelName }));
				},
				onError: (error) =>
					toast.error(errorMessage(error, t("pages.modelFit.inferenceProfiles.actions.benchmarkError", "Could not benchmark the profile."))),
			},
		);
	};

	const handleFreeze = (profile: InferenceProfileView): void => {
		freeze.mutate(
			{ profileId: profile.id },
			{
				onSuccess: () =>
					toast.success(t("pages.modelFit.inferenceProfiles.actions.freezeSuccess", "Froze {{model}}.", { model: profile.modelName })),
				onError: (error) =>
					toast.error(errorMessage(error, t("pages.modelFit.inferenceProfiles.actions.freezeError", "Could not freeze the profile."))),
			},
		);
	};

	const handleInvalidate = (profile: InferenceProfileView): void => {
		invalidate.mutate(
			{ profileId: profile.id },
			{
				onSuccess: () =>
					toast.success(t("pages.modelFit.inferenceProfiles.actions.invalidateSuccess", "Invalidated {{model}}.", { model: profile.modelName })),
				onError: (error) =>
					toast.error(errorMessage(error, t("pages.modelFit.inferenceProfiles.actions.invalidateError", "Could not invalidate the profile."))),
			},
		);
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="inference-profile-panel">
			<Stack gap="md">
				<Group gap="xs" align="center">
					<IconSettings size={20} />
					<Title order={4}>{t("pages.modelFit.inferenceProfiles.title", "Inference profiles")}</Title>
				</Group>
				<Text c="dimmed" size="sm">
					{t(
						"pages.modelFit.inferenceProfiles.subtitle",
						"Tuned launch profiles this node explored, benchmarked, and froze for repeat use. Outcomes only — tokens/second and VRAM.",
					)}
				</Text>

				<Group align="flex-end" gap="sm" wrap="wrap">
					<TextInput
						label={t("pages.modelFit.inferenceProfiles.explore.modelLabel", "Model name")}
						placeholder={t("pages.modelFit.inferenceProfiles.explore.modelPlaceholder", "e.g. unsloth/Qwen3-4B-GGUF")}
						value={modelName}
						onChange={(event) => setModelName(event.currentTarget.value)}
						data-testid="inference-profile-explore-model"
						style={{ flex: 1, minWidth: 200, maxWidth: 320 }}
					/>
					<Select
						label={t("pages.modelFit.inferenceProfiles.explore.roleLabel", "Role")}
						data={roleData}
						value={role}
						onChange={(value) => setRole(value ?? "chat")}
						allowDeselect={false}
						data-testid="inference-profile-explore-role"
						w={180}
					/>
					<Button
						leftSection={<IconPlayerPlay size={16} />}
						loading={explore.isPending}
						disabled={modelName.trim().length === 0}
						onClick={handleExplore}
						data-testid="inference-profile-explore-button"
					>
						{t("pages.modelFit.inferenceProfiles.explore.button", "Explore")}
					</Button>
				</Group>

				{profilesQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.modelFit.inferenceProfiles.loading", "Loading inference profiles…")}</Text>
					</Group>
				) : null}

				{profilesQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="inference-profile-error">
						{errorMessage(profilesQuery.error, t("pages.modelFit.inferenceProfiles.error", "Could not load inference profiles."))}
					</Alert>
				) : null}

				{!profilesQuery.isLoading && !profilesQuery.error && profiles.length === 0 ? (
					<Alert color="gray" icon={<IconInfoCircle size={16} />} data-testid="inference-profile-empty">
						{t("pages.modelFit.inferenceProfiles.empty", "No inference profiles yet. Explore a model to create one.")}
					</Alert>
				) : null}

				{!profilesQuery.isLoading && !profilesQuery.error && profiles.length > 0 ? (
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
								{profiles.map((profile) => {
									const result = benchmarkResults[profile.id];
									const tokensPerSecond = result?.metrics?.tokensPerSecond ?? null;
									const vramBytes = profile.frozenVramBytes ?? result?.metrics?.vramAfterBytes ?? null;
									const summary = formatProfileOutcomeSummary(tokensPerSecond, vramBytes);
									const canFreeze = profile.hasBenchmark || result !== undefined;
									const isBenchmarking = benchmark.isPending && benchmark.variables?.profileId === profile.id;
									const isFreezing = freeze.isPending && freeze.variables?.profileId === profile.id;
									const isInvalidating = invalidate.isPending && invalidate.variables?.profileId === profile.id;

									const freezeButton = (
										<Button
											size="xs"
											variant="light"
											color="cyan"
											leftSection={<IconSnowflake size={14} />}
											loading={isFreezing}
											disabled={!canFreeze}
											onClick={() => handleFreeze(profile)}
											data-testid={`inference-profile-freeze-${profile.id}`}
										>
											{t("pages.modelFit.inferenceProfiles.actions.freeze", "Freeze")}
										</Button>
									);

									return (
										<Fragment key={profile.id}>
											<Table.Tr data-testid={`inference-profile-row-${profile.id}`}>
												<Table.Td>
													<Badge color={statusColor[profile.status]} variant="light" data-testid={`inference-profile-status-${profile.id}`}>
														{t(`pages.modelFit.inferenceProfiles.status.${profile.status}`, profile.status)}
													</Badge>
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
													{summary ? (
														<Text size="sm" data-testid={`inference-profile-outcome-${profile.id}`}>
															{summary}
														</Text>
													) : (
														<Text size="sm" c="dimmed" data-testid={`inference-profile-outcome-${profile.id}`}>
															—
														</Text>
													)}
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
																	onClick={() => handleBenchmark(profile)}
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
																onClick={() => handleInvalidate(profile)}
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
										</Fragment>
									);
								})}
							</Table.Tbody>
						</Table>
					</Table.ScrollContainer>
				) : null}
			</Stack>
		</Card>
	);
}
