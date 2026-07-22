import {
	Alert,
	Badge,
	Button,
	Card,
	Checkbox,
	Group,
	List,
	Select,
	Stack,
	Text,
	TextInput,
	ThemeIcon,
	Title,
} from "@mantine/core";
import { IconAlertTriangle, IconCircleCheck, IconCircleX, IconPlayerStop, IconReload, IconTrash } from "@tabler/icons-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { toast } from "@/core/ui/notifications/Toast";
import { CudaBuildLogView } from "@/features/node-settings/components/CudaBuildLogView";
import { useSourceBuildHub } from "@/features/node-settings/hooks/useSourceBuildHub";
import type {
	LlamaCppSourceBackend,
	LlamaCppSourceSelection,
	SourceBuildDraft,
} from "@/features/node-settings/models/SourceBuildModels";
import { sourceBuildValidationError } from "@/features/node-settings/models/SourceBuildModels";
import {
	useCancelSourceBuild,
	useLlamaCppRuntimeStatus,
	useRemoveSourceBuild,
	useSourceBuildPrerequisites,
	useSourceBuildStatus,
	useStartSourceBuild,
} from "@/features/node-settings/queries/useLocalRuntime";

function errorMessage(error: unknown, fallback: string): string {
	if (error !== null && typeof error === "object" && "response" in error) {
		const data = (error as { response?: { data?: { message?: unknown } } }).response?.data;
		if (typeof data?.message === "string") {
			return data.message;
		}
	}
	return error instanceof Error ? error.message : fallback;
}

export function SourceBuildCard() {
	const { t } = useTranslation();
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const [backend, setBackend] = useState<LlamaCppSourceBackend>("cpu");
	const [source, setSource] = useState<LlamaCppSourceSelection>("official");
	const [repository, setRepository] = useState("");
	const [commit, setCommit] = useState("");
	const [acknowledged, setAcknowledged] = useState(false);
	const prerequisites = useSourceBuildPrerequisites(backend, developerMode);
	const status = useSourceBuildStatus(developerMode);
	const runtime = useLlamaCppRuntimeStatus(developerMode);
	const start = useStartSourceBuild();
	const cancel = useCancelSourceBuild();
	const remove = useRemoveSourceBuild();
	const hub = useSourceBuildHub(developerMode);

	const installed = runtime.data?.installed;
	useEffect(() => {
		if (!installed?.isSourceBuild) {
			return;
		}
		if (installed.variant === "cpu" || installed.variant === "vulkan" || installed.variant === "cuda") {
			setBackend(installed.variant);
		}
		if (installed.sourceRepository && installed.sourceRepository !== "https://github.com/ggml-org/llama.cpp") {
			setSource("custom");
			setRepository(installed.sourceRepository);
			setAcknowledged(true);
		}
		setCommit(installed.sourceRevisionMode === "explicitCommit" ? (installed.sourceRequestedCommit ?? "") : "");
	}, [installed]);

	if (!developerMode) {
		return null;
	}

	const draft: SourceBuildDraft = { backend, source, repository, commit, acknowledgeCustomSourceRisk: acknowledged };
	const validationError = sourceBuildValidationError(draft);
	const buildStatus = status.data;
	const isBuilding = buildStatus?.isRunning === true || start.isPending;
	const runningProcesses = runtime.data?.runningProcessCount ?? 0;
	const current = buildStatus?.currentBuild;
	const livePhase = hub.phase ?? buildStatus?.phase ?? null;
	const liveLogs = hub.logLines.length > 0 ? hub.logLines : (buildStatus?.logLines ?? []);
	const liveError = hub.error ?? buildStatus?.sanitizedError ?? null;

	const run = (): void => {
		hub.reset();
		start.mutate(draft, { onError: (error) => toast.error(errorMessage(error, "Could not start the source build.")) });
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="source-build-card">
			<Stack gap="md">
				<Group gap="xs">
					<Title order={4}>{t("pages.nodeSettings.llamaCpp.sourceBuild.title", "llama.cpp build from source")}</Title>
					<Badge color="grape">Dev</Badge>
				</Group>
				<Text size="sm" c="dimmed">
					{t(
						"pages.nodeSettings.llamaCpp.sourceBuild.description",
						"Build CPU, Vulkan, or CUDA llama.cpp from official upstream or a trusted public GitHub fork.",
					)}
				</Text>
				<Group grow={true} align="start">
					<Select
						label="Backend"
						value={backend}
						data={["cpu", "vulkan", "cuda"]}
						onChange={(value) => value && setBackend(value as LlamaCppSourceBackend)}
					/>
					<Select
						label="Source"
						value={source}
						data={["official", "custom"]}
						onChange={(value) => value && setSource(value as LlamaCppSourceSelection)}
					/>
				</Group>
				{source === "custom" ? (
					<Stack gap="sm">
						<TextInput
							label="GitHub repository"
							placeholder="https://github.com/owner/llama.cpp"
							value={repository}
							onChange={(event) => setRepository(event.currentTarget.value)}
						/>
						<Alert color="red" icon={<IconAlertTriangle size={16} />}>
							Custom repository code executes with the app user's privileges. It is trusted code, not sandboxed.
						</Alert>
						<Checkbox
							checked={acknowledged}
							onChange={(event) => setAcknowledged(event.currentTarget.checked)}
							label="I trust this public GitHub repository and accept the code-execution risk."
						/>
					</Stack>
				) : null}
				<TextInput label="Commit SHA (optional)" value={commit} onChange={(event) => setCommit(event.currentTarget.value)} />

				<List spacing="xs" size="sm">
					{(prerequisites.data?.items ?? []).map((item) => (
						<List.Item
							key={item.key}
							icon={
								<ThemeIcon color={item.satisfied ? "green" : "red"} size={20} radius="xl" variant="light">
									{item.satisfied ? <IconCircleCheck size={14} /> : <IconCircleX size={14} />}
								</ThemeIcon>
							}
						>
							<Text span={true} fw={500}>
								{item.key}
							</Text>{" "}
							<Text span={true} c="dimmed">
								{item.detail}
							</Text>
						</List.Item>
					))}
				</List>
				{validationError ? <Alert color="yellow">{validationError}</Alert> : null}
				{liveError ? <Alert color="red">{liveError}</Alert> : null}
				{isBuilding ? <CudaBuildLogView phase={livePhase} logLines={liveLogs} /> : null}
				{installed?.isSourceBuild ? (
					<Stack gap="xs">
						<Group>
							<Badge color="green">Active {installed.variant} source runtime</Badge>
							{installed.sourceCommit ? <Text ff="monospace">{installed.sourceCommit.slice(0, 12)}</Text> : null}
						</Group>
						{current ? (
							<Text size="sm" c="dimmed">
								{current.repository} · {current.revisionMode}
							</Text>
						) : null}
					</Stack>
				) : null}
				<Group>
					<Button
						leftSection={<IconReload size={16} />}
						onClick={run}
						loading={start.isPending}
						disabled={validationError !== null || prerequisites.data?.canBuild !== true || isBuilding || runningProcesses > 0}
					>
						{installed?.isSourceBuild ? "Rebuild" : "Build"}
					</Button>
					{isBuilding ? (
						<Button
							color="yellow"
							leftSection={<IconPlayerStop size={16} />}
							onClick={() => cancel.mutate()}
							loading={cancel.isPending}
						>
							Cancel
						</Button>
					) : null}
					{installed?.isSourceBuild ? (
						<Button
							color="red"
							variant="light"
							leftSection={<IconTrash size={16} />}
							disabled={isBuilding || runningProcesses > 0}
							onClick={() =>
								remove.mutate(undefined, {
									onError: (error) => toast.error(errorMessage(error, "Could not remove the source runtime.")),
								})
							}
						>
							Remove
						</Button>
					) : null}
				</Group>
			</Stack>
		</Card>
	);
}
