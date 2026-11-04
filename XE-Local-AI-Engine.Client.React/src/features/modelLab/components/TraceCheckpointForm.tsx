import {
	Alert,
	Button,
	Checkbox,
	Group,
	Paper,
	Progress,
	SegmentedControl,
	Select,
	Stack,
	Text,
	Textarea,
	TextInput,
} from "@mantine/core";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	getBaseArtifactOptions,
	getBaseArtifactQueryKey,
	listBaseArtifactsOptions,
	listLocalModelsOptions,
	listModelLabExperimentsQueryKey,
	preflightModelLabCheckpointMutation,
	startModelLabCheckpointDownloadMutation,
	startModelLabExperimentMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { formatBytesAsGb } from "@/core/formatting/BytesFormatting";
import { LLAMACPP_PROVIDER } from "@/core/models/LocalModelProviders";
import { ModelPickerNotice } from "@/features/modelLab/components/ModelPickerNotice";
import { SteeringForm } from "@/features/modelLab/components/SteeringForm";
import { starterSteeringDraft } from "@/features/modelLab/models/ModelLabSteering";

// The transfer has no hub of its own, so the page reads its progress while it runs.
const downloadPollMs = 5_000;

// Pick a checkpoint and a GGUF, check them, confirm the license, download, then trace. Each step is refused by the server before the next can cost anything.
export function TraceCheckpointForm({ busy }: { busy: boolean }) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const models = useQuery({ ...listLocalModelsOptions(), staleTime: 30_000 });
	const ggufNames = (models.data?.items ?? []).flatMap((item) =>
		item.provider === LLAMACPP_PROVIDER && item.modelName ? [item.modelName] : [],
	);
	const [installedModelName, setInstalledModelName] = useState<string | null>(null);
	const saved = useQuery(listBaseArtifactsOptions());
	const savedOptions = (saved.data?.items ?? []).flatMap((item) =>
		item.status === "Ready" && item.id ? [{ value: item.id, label: `${item.repoId} @ ${item.revision?.slice(0, 12)}` }] : [],
	);
	const [savedId, setSavedId] = useState<string | null>(null);
	const [repoId, setRepoId] = useState("");
	const [revision, setRevision] = useState("");
	const [licenseConfirmed, setLicenseConfirmed] = useState(false);
	const [prompt, setPrompt] = useState("");
	const [jobKind, setJobKind] = useState("trace");
	const [steeringDraft, setSteeringDraft] = useState(starterSteeringDraft);

	const preflight = useMutation(preflightModelLabCheckpointMutation());
	const download = useMutation({
		...startModelLabCheckpointDownloadMutation(),
		// A retry reuses the failed row, and the page must read it again to see the new transfer.
		onSuccess: (data) =>
			queryClient.invalidateQueries({ queryKey: getBaseArtifactQueryKey({ path: { artifactId: data.baseArtifactId ?? "" } }) }),
	});
	const start = useMutation({
		...startModelLabExperimentMutation(),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: listModelLabExperimentsQueryKey() }),
	});
	const artifactId = savedId ?? download.data?.baseArtifactId ?? null;
	const artifact = useQuery({
		...getBaseArtifactOptions({ path: { artifactId: artifactId ?? "" } }),
		enabled: artifactId !== null,
		refetchInterval: (query) => (query.state.data?.status === "Downloading" ? downloadPollMs : false),
	});

	const pick = (update: () => void): void => {
		update();
		preflight.reset();
		download.reset();
		start.reset();
		setLicenseConfirmed(false);
	};

	const body = { repoId: repoId.trim(), revision: revision.trim() || undefined, installedModelName: installedModelName ?? "" };
	const checkBody = savedId ? { baseArtifactId: savedId, installedModelName: body.installedModelName } : body;
	const check = preflight.data;
	const refusal = download.data?.preflight?.refusalMessage ?? check?.refusalMessage;
	const allowed = check !== undefined && check.refusal === null;
	const ready = artifact.data?.status === "Ready";
	const progress = artifact.data?.progress;

	return (
		<Paper withBorder={true} p="md">
			<Stack gap="sm">
				<ModelPickerNotice status={models.status} modelCount={ggufNames.length} />
				<Text size="sm" c="dimmed">
					{t(
						"pages.modelLab.trace.scope",
						"Tracing loads the full-precision checkpoint on the GPU, so it needs the checkpoint's weights plus about 2 GB of the card's memory. A model above that is refused before anything is downloaded, however it was quantized for chat.",
					)}
				</Text>
				<Select
					label={t("pages.modelLab.trace.gguf", "Installed GGUF")}
					description={t("pages.modelLab.trace.ggufHint", "The checkpoint is paired with it by embedding size and block count.")}
					placeholder={t("pages.directInference.modelPlaceholder", "Choose a model")}
					data={ggufNames}
					value={installedModelName}
					onChange={(value) => pick(() => setInstalledModelName(value))}
					searchable={true}
				/>
				{saved.isError && (
					<Alert color="yellow" data-testid="model-lab-saved-failed">
						{apiErrorMessage(
							saved.error,
							t("pages.modelLab.trace.savedFailed", "The checkpoints on this node could not be read."),
						)}
					</Alert>
				)}
				{savedOptions.length > 0 && (
					<Select
						label={t("pages.modelLab.trace.saved", "Checkpoint on this node")}
						description={t(
							"pages.modelLab.trace.savedHint",
							"Already downloaded, so it is checked and traced without contacting Hugging Face.",
						)}
						data={savedOptions}
						value={savedId}
						onChange={(value) => pick(() => setSavedId(value))}
						clearable={true}
					/>
				)}
				<Group grow={true} align="flex-start">
					<TextInput
						label={t("pages.modelLab.trace.repo", "Checkpoint repository")}
						placeholder="Qwen/Qwen3.5-0.8B"
						disabled={savedId !== null}
						value={repoId}
						onChange={(event) => {
							const { value } = event.currentTarget;
							pick(() => setRepoId(value));
						}}
					/>
					<TextInput
						label={t("pages.modelLab.trace.revision", "Revision (optional)")}
						disabled={savedId !== null}
						value={revision}
						onChange={(event) => {
							const { value } = event.currentTarget;
							pick(() => setRevision(value));
						}}
					/>
				</Group>
				<Group>
					<Button
						variant="default"
						disabled={installedModelName === null || (savedId === null && repoId.trim().length === 0)}
						loading={preflight.isPending}
						onClick={() => preflight.mutate({ body: checkBody })}
						data-testid="model-lab-check"
					>
						{t("pages.modelLab.trace.check", "Check")}
					</Button>
				</Group>
				{artifact.isError && (
					<Alert color="red" data-testid="model-lab-artifact-failed">
						{apiErrorMessage(
							artifact.error,
							t("pages.modelLab.trace.artifactFailed", "The checkpoint's state could not be read."),
						)}
					</Alert>
				)}
				{preflight.isError && (
					<Alert color="red">
						{apiErrorMessage(preflight.error, t("pages.modelLab.trace.checkFailed", "The check could not run."))}
					</Alert>
				)}
				{refusal && (
					<Alert color="red" data-testid="model-lab-refusal">
						{refusal}
					</Alert>
				)}
				{allowed && (
					<>
						<Text size="sm" data-testid="model-lab-fit">
							{t("pages.modelLab.trace.fit", "Needs about {{required}} of this card's {{card}}. Commit {{revision}}.", {
								required: formatBytesAsGb(check.requiredVramBytes ?? null),
								card: formatBytesAsGb(check.cardVramBytes ?? null),
								revision: check.revision?.slice(0, 12),
							})}
						</Text>
						<Checkbox
							label={check.licenseConfirmationText}
							checked={licenseConfirmed}
							onChange={(event) => setLicenseConfirmed(event.currentTarget.checked)}
							data-testid="model-lab-license"
						/>
						{savedId === null && (
							<Group>
								<Button
									variant="default"
									disabled={!licenseConfirmed || (artifactId !== null && artifact.data?.status !== "Failed")}
									loading={download.isPending}
									onClick={() => download.mutate({ body: { ...body, revision: check.revision ?? undefined, licenseConfirmed } })}
									data-testid="model-lab-download"
								>
									{t("pages.modelLab.trace.download", "Get the checkpoint")}
								</Button>
								{artifact.data && (
									<Text size="sm" data-testid="model-lab-artifact-status">
										{t(`pages.modelLab.trace.artifact.${artifact.data.status}`, artifact.data.status ?? "")}
									</Text>
								)}
							</Group>
						)}
						{artifact.data?.status === "Downloading" && progress?.totalBytes && (
							<Progress value={(100 * (progress.completedBytes ?? 0)) / progress.totalBytes} />
						)}
						{artifact.data?.errorMessage && <Alert color="red">{artifact.data.errorMessage}</Alert>}
					</>
				)}
				{download.isError && (
					<Alert color="red">
						{apiErrorMessage(download.error, t("pages.modelLab.trace.downloadFailed", "The download could not start."))}
					</Alert>
				)}
				{ready && allowed && (
					<>
						<SegmentedControl
							value={jobKind}
							onChange={setJobKind}
							w="fit-content"
							data={[
								{ value: "trace", label: t("pages.modelLab.steer.jobTrace", "Trace") },
								{ value: "steering", label: t("pages.modelLab.steer.jobSteering", "Steering") },
							]}
							data-testid="model-lab-job-kind"
						/>
						{jobKind === "steering" ? (
							<SteeringForm
								defaultLayer={check.defaultSteeringLayer}
								steerableBlockCount={check.steerableBlockCount}
								layerTypes={check.checkpointLayerTypes}
								draft={steeringDraft}
								onDraftChange={setSteeringDraft}
								disabled={busy || !licenseConfirmed}
								loading={start.isPending}
								onStart={(run) =>
									start.mutate({
										body: {
											baseArtifactId: artifactId ?? "",
											installedModelName: installedModelName ?? "",
											licenseConfirmed,
											...run,
										},
									})
								}
							/>
						) : (
							<>
								<Textarea
									label={t("pages.modelLab.trace.prompt", "Prompt")}
									autosize={true}
									minRows={2}
									maxLength={8000}
									value={prompt}
									onChange={(event) => setPrompt(event.currentTarget.value)}
								/>
								<Text size="xs" c="dimmed">
									{t(
										"pages.modelLab.trace.spec",
										"Three layers across the stack, up to 128 tokens, greedy. The trace is capped in size and saved encrypted on this node, with the prompt and the output.",
									)}
								</Text>
								<Group>
									<Button
										disabled={busy || prompt.trim().length === 0 || !licenseConfirmed}
										loading={start.isPending}
										onClick={() =>
											start.mutate({
												body: {
													baseArtifactId: artifactId ?? "",
													installedModelName: installedModelName ?? "",
													prompt,
													licenseConfirmed,
												},
											})
										}
										data-testid="model-lab-start"
									>
										{t("pages.modelLab.trace.start", "Start trace")}
									</Button>
								</Group>
							</>
						)}
						{start.isError && (
							<Alert color="yellow" data-testid="model-lab-start-refused">
								{apiErrorMessage(start.error, t("pages.modelLab.trace.startFailed", "The experiment could not start."))}
							</Alert>
						)}
					</>
				)}
			</Stack>
		</Paper>
	);
}
