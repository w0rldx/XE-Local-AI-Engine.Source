import {
	Alert,
	Button,
	Checkbox,
	Group,
	NumberInput,
	Paper,
	Progress,
	SegmentedControl,
	Select,
	Stack,
	Switch,
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
import type {
	XeLocalAiEngineClientServicesModelLabModelLabAttentionRefusal as AttentionRefusal,
	XeLocalAiEngineClientServicesModelLabModelLabTraceRefusal as TraceRefusal,
} from "@/core/api/generated/types.gen";
import { formatBytesAsGb } from "@/core/formatting/BytesFormatting";
import { LLAMACPP_PROVIDER } from "@/core/models/LocalModelProviders";
import { FieldWithInfo } from "@/core/ui/components/FieldInfo/FieldInfo";
import { ModelPickerNotice } from "@/features/modelLab/components/ModelPickerNotice";
import { SteeringForm } from "@/features/modelLab/components/SteeringForm";
import { TraceLayerPicker } from "@/features/modelLab/components/TraceLayerPicker";
import { licenseConfirmationLabel } from "@/features/modelLab/models/ModelLabLicense";
import { installedModelsStaleMs } from "@/features/modelLab/models/ModelLabNavigation";
import { starterSteeringDraft } from "@/features/modelLab/models/ModelLabSteering";

// The transfer has no hub of its own, so the page reads its progress while it runs.
const downloadPollMs = 5_000;

// The server's default and bound for a trace's token count (ModelLabTraceSpec.MaxNewTokens, MaxRequestedNewTokens).
const defaultNewTokens = 128;
const maxNewTokensLimit = 256;

// Pick a checkpoint and a GGUF, check them, confirm the license, download, then trace. Each step is refused by the server before the next can cost anything.
export function TraceCheckpointForm({ busy }: { busy: boolean }) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const models = useQuery({ ...listLocalModelsOptions(), staleTime: installedModelsStaleMs });
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
	const [layers, setLayers] = useState<number[]>([]);
	const [recordAttention, setRecordAttention] = useState(false);
	const [maxNewTokens, setMaxNewTokens] = useState<number | string>(defaultNewTokens);

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
		// Another checkpoint can have fewer blocks, or no full-attention block.
		setLayers([]);
		setRecordAttention(false);
	};

	const body = { repoId: repoId.trim(), revision: revision.trim() || undefined, installedModelName: installedModelName ?? "" };
	const checkBody = savedId ? { baseArtifactId: savedId, installedModelName: body.installedModelName } : body;
	const check = preflight.data;
	const refused = download.data?.preflight?.refusalMessage ? download.data.preflight : check;
	// The server's message for this code is the English confirmation, so it is rebuilt from the same facts in the page's language.
	const refusal = refused?.refusal === "LicenseNotConfirmed" ? licenseConfirmationLabel(t, refused) : refused?.refusalMessage;
	const allowed = check !== undefined && check.refusal === null;
	const licenseLabel = allowed ? licenseConfirmationLabel(t, check) : "";
	// The English text is what the node records; it is offered beside a translation, which it would only repeat in English.
	const licenseOriginal =
		allowed && check.licenseConfirmationText && check.licenseConfirmationText !== licenseLabel
			? `${t("pages.modelLab.trace.license.englishOriginalNote", "This English text is what the node records as your confirmation.")}\n\n${check.licenseConfirmationText}`
			: undefined;
	const ready = artifact.data?.status === "Ready";
	const progress = artifact.data?.progress;
	// Only a full-attention block has weights over earlier positions, and only a family and config the node can reconstruct
	// are recorded; the server refuses the rest and says why.
	const canRecordAttention = check?.checkpointLayerTypes?.includes("full_attention") === true && check.attentionSupported;
	// Each code in the page's language; the server's English text when it gives none.
	const attentionRefusals: Record<AttentionRefusal, string> = {
		HeadCountOutOfRange: t(
			"pages.modelLab.trace.recordAttention.refusal.headCountOutOfRange",
			"This checkpoint has more attention heads per block than a recording holds. Start the trace without it.",
		),
		NoFullAttentionBlock: t(
			"pages.modelLab.trace.recordAttention.unavailable",
			"This checkpoint has no full-attention block, so there is no attention to record.",
		),
		NotFullAttention: t(
			"pages.modelLab.trace.recordAttention.refusal.notFullAttention",
			"This OLMoE checkpoint's config describes attention other than full attention at every block, and attention is recorded only for full attention. Start the trace without it.",
		),
		ShapeNotStated: t(
			"pages.modelLab.trace.recordAttention.refusal.shapeNotStated",
			"This checkpoint's config does not state its block kinds and attention heads, so attention cannot be recorded. Start the trace without it.",
		),
		UnsupportedModelType: t(
			"pages.modelLab.trace.recordAttention.refusal.unsupportedModelType",
			"Attention capture is implemented for Qwen3.5 and OLMoE checkpoints; start the trace without it.",
		),
	};
	const traceRefusals: Record<TraceRefusal, string> = {
		RouterLogitsRequested: t(
			"pages.modelLab.trace.traceRefusal.routerLogitsRequested",
			"This checkpoint's config sets output_router_logits, which a trace does not support. It can still be steered.",
		),
	};
	const attentionRefusal =
		(check?.attentionRefusal && attentionRefusals[check.attentionRefusal]) || check?.attentionRefusalMessage;
	// A checkpoint the script cannot trace can still be steered.
	const traceRefusal = (check?.traceRefusal && traceRefusals[check.traceRefusal]) || check?.traceRefusalMessage;
	const recordAttentionLabel = t("pages.modelLab.trace.recordAttention.label", "Record attention");
	const maxNewTokensLabel = t("pages.modelLab.trace.maxNewTokens.label", "Tokens to generate");

	return (
		<Paper withBorder={true} p="md">
			<Stack gap="sm">
				<ModelPickerNotice status={models.status} modelCount={ggufNames.length} />
				<Text size="sm" c="dimmed">
					{t(
						"pages.modelLab.trace.scope",
						"Tracing loads the full-precision checkpoint on the GPU, so it needs the checkpoint's weights plus about 2 GiB of the card's memory. A model above that is refused before anything is downloaded, however it was quantized for chat.",
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
						{/* Shown before the download, which steering alone may still be worth. */}
						{traceRefusal && (
							<Alert color="yellow" data-testid="model-lab-trace-refused">
								{traceRefusal}
							</Alert>
						)}
						<FieldWithInfo
							label={t("pages.modelLab.trace.license.englishOriginal", "English original")}
							text={licenseOriginal}
							testId="model-lab-license-original"
						>
							<Checkbox
								label={licenseLabel}
								checked={licenseConfirmed}
								onChange={(event) => setLicenseConfirmed(event.currentTarget.checked)}
								data-testid="model-lab-license"
							/>
						</FieldWithInfo>
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
							aria-label={t("pages.modelLab.steer.jobKindLabel", "Kind of run")}
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
								{check.checkpointBlockCount ? (
									<TraceLayerPicker blockCount={check.checkpointBlockCount} value={layers} onChange={setLayers} disabled={busy} />
								) : null}
								<FieldWithInfo
									label={recordAttentionLabel}
									text={t(
										"pages.modelLab.trace.recordAttention.help",
										"Also stores, for every generated token, where each head of the full-attention blocks read from: the top keys of its row and the weight of the rest. The model's output is unchanged.\n\nIt costs a few MiB of storage per run and makes the trace slightly slower. Turn it on to step through a run's attention; leave it off when you only need norms and candidates, or to keep runs small.",
									)}
									testId="model-lab-record-attention-info"
								>
									<Switch
										label={recordAttentionLabel}
										description={
											canRecordAttention
												? undefined
												: (attentionRefusal ??
													t(
														"pages.modelLab.trace.recordAttention.unavailable",
														"This checkpoint has no full-attention block, so there is no attention to record.",
													))
										}
										checked={recordAttention && canRecordAttention}
										onChange={(event) => setRecordAttention(event.currentTarget.checked)}
										disabled={busy || !canRecordAttention}
										data-testid="model-lab-record-attention"
									/>
								</FieldWithInfo>
								<FieldWithInfo
									label={maxNewTokensLabel}
									text={t(
										"pages.modelLab.trace.maxNewTokens.help",
										"How many tokens the trace generates at most; it stops earlier when the model ends its answer. 128 is the usual choice.\n\nMore tokens follow a longer answer but take longer and store more: the trace and the attention rows grow with each token. Past about 128 tokens a run may keep the next-token candidates for its first steps only, so its saved result stays within its size limit. Lower it for a quick look at the start of an answer.",
									)}
									testId="model-lab-max-new-tokens-info"
								>
									<NumberInput
										label={maxNewTokensLabel}
										min={1}
										max={maxNewTokensLimit}
										clampBehavior="strict"
										allowDecimal={false}
										allowNegative={false}
										value={maxNewTokens}
										onChange={setMaxNewTokens}
										disabled={busy}
										data-testid="model-lab-max-new-tokens"
									/>
								</FieldWithInfo>
								<Text size="xs" c="dimmed">
									{t(
										"pages.modelLab.trace.spec",
										"Greedy. The trace is capped in size by the number of blocks and tokens and saved encrypted on this node, with the prompt and the output.",
									)}
								</Text>
								<Group>
									<Button
										disabled={busy || prompt.trim().length === 0 || !licenseConfirmed || Boolean(traceRefusal)}
										loading={start.isPending}
										onClick={() =>
											start.mutate({
												body: {
													baseArtifactId: artifactId ?? "",
													installedModelName: installedModelName ?? "",
													prompt,
													licenseConfirmed,
													...(layers.length > 0 ? { layers } : {}),
													...(recordAttention && canRecordAttention ? { recordAttention: true } : {}),
													// An emptied field asks for the server's default.
													...(typeof maxNewTokens === "number" ? { maxNewTokens } : {}),
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
