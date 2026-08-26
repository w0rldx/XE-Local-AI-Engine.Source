import { Card, Group, Stack, Text, Title } from "@mantine/core";
import { IconSettings } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { toast } from "@/core/ui/notifications/Toast";
import { InferenceProfileExploreForm } from "@/features/model-fit/components/InferenceProfileExploreForm";
import { InferenceProfileList } from "@/features/model-fit/components/InferenceProfileList";
import type { InferenceBenchmarkResult, InferenceProfileView } from "@/features/model-fit/models/InferenceProfileModels";
import {
	useBenchmarkInferenceProfile,
	useExploreInferenceProfile,
	useFreezeInferenceProfile,
	useInferenceProfiles,
	useInvalidateInferenceProfile,
} from "@/features/model-fit/queries/useInferenceProfiles";

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
	// `role` is the llama-server ModelRole on the wire.
	const [role, setRole] = useState<string>("chat");
	const [allowPreSpawnVramPressure, setAllowPreSpawnVramPressure] = useState(false);
	// Ephemeral, per-row benchmark outcomes keyed by profile id — enriches the outcome line + renders the metrics card.
	const [benchmarkResults, setBenchmarkResults] = useState<Record<string, InferenceBenchmarkResult>>({});

	const profiles = profilesQuery.data ?? [];

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
					toast.error(
						apiErrorMessage(error, t("pages.modelFit.inferenceProfiles.explore.error", "Could not start exploration.")),
					),
			},
		);
	};

	const handleBenchmark = (profile: InferenceProfileView): void => {
		benchmark.mutate(
			{ profileId: profile.id, allowPreSpawnVramPressure },
			{
				onSuccess: (result) => {
					setBenchmarkResults((previous) => ({ ...previous, [profile.id]: result }));
					toast.success(
						t("pages.modelFit.inferenceProfiles.actions.benchmarkSuccess", "Benchmarked {{model}}.", {
							model: profile.modelName,
						}),
					);
				},
				onError: (error) =>
					toast.error(
						apiErrorMessage(
							error,
							t("pages.modelFit.inferenceProfiles.actions.benchmarkError", "Could not benchmark the profile."),
						),
					),
			},
		);
	};

	const handleFreeze = (profile: InferenceProfileView): void => {
		freeze.mutate(
			{ profileId: profile.id },
			{
				onSuccess: () =>
					toast.success(
						t("pages.modelFit.inferenceProfiles.actions.freezeSuccess", "Froze {{model}}.", { model: profile.modelName }),
					),
				onError: (error) =>
					toast.error(
						apiErrorMessage(error, t("pages.modelFit.inferenceProfiles.actions.freezeError", "Could not freeze the profile.")),
					),
			},
		);
	};

	const handleInvalidate = (profile: InferenceProfileView): void => {
		invalidate.mutate(
			{ profileId: profile.id },
			{
				onSuccess: () =>
					toast.success(
						t("pages.modelFit.inferenceProfiles.actions.invalidateSuccess", "Invalidated {{model}}.", {
							model: profile.modelName,
						}),
					),
				onError: (error) =>
					toast.error(
						apiErrorMessage(
							error,
							t("pages.modelFit.inferenceProfiles.actions.invalidateError", "Could not invalidate the profile."),
						),
					),
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

				<InferenceProfileExploreForm
					allowPreSpawnVramPressure={allowPreSpawnVramPressure}
					isPending={explore.isPending}
					modelName={modelName}
					onAllowPreSpawnVramPressureChange={setAllowPreSpawnVramPressure}
					onExplore={handleExplore}
					onModelNameChange={setModelName}
					onRoleChange={setRole}
					role={role}
				/>

				<InferenceProfileList
					benchmarkResults={benchmarkResults}
					loadState={{ loading: profilesQuery.isLoading, error: profilesQuery.error }}
					pending={{
						benchmarkProfileId: benchmark.isPending ? benchmark.variables?.profileId : undefined,
						freezeProfileId: freeze.isPending ? freeze.variables?.profileId : undefined,
						invalidateProfileId: invalidate.isPending ? invalidate.variables?.profileId : undefined,
					}}
					onBenchmark={handleBenchmark}
					onFreeze={handleFreeze}
					onInvalidate={handleInvalidate}
					profiles={profiles}
				/>
			</Stack>
		</Card>
	);
}
