import { Alert, Badge, Button, Code, Group, Loader, Select, Stack, Tabs, Text } from "@mantine/core";
import { IconArrowBackUp } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelDetailsResponse } from "@/core/api/generated";
import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { ModelFitPanel } from "@/features/models/components/ModelFitPanel";
import { ModelLaunchArgumentsPanel } from "@/features/models/components/ModelLaunchArgumentsPanel";
import type { LocalModelViewModel } from "@/features/models/models/LocalModelModel";
import { buildKindOptions, capabilityLabel, kindBadgeColor, kindLabel } from "@/features/models/models/ModelKindFormatters";

type LocalModelDetails = XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelDetailsResponse;

interface ModelDetailsDialogProps {
	opened: boolean;
	onClose: () => void;
	model: LocalModelViewModel | undefined;
	details: LocalModelDetails | undefined;
	detailsLoading: boolean;
	isActionPending: boolean;
	modelFitEnabled: boolean;
	onSetKind: (modelName: string, kind: string) => void;
	onResetKind: (modelName: string) => void;
}

interface ModelDetailsBodyProps {
	model: LocalModelViewModel;
	details: LocalModelDetails | undefined;
	detailsLoading: boolean;
	isActionPending: boolean;
	modelFitEnabled: boolean;
	onSetKind: (modelName: string, kind: string) => void;
	onResetKind: (modelName: string) => void;
}

// The tabbed body of the model details dialog. Split out so it mounts fresh each time the dialog opens (Mantine
// Modal unmounts children when closed) — that resets the active tab and means the Fit tab's cache-only llmfit query
// only fires once the operator opens the dialog and switches to that tab.
function ModelDetailsBody({
	model,
	details,
	detailsLoading,
	isActionPending,
	modelFitEnabled,
	onSetKind,
	onResetKind,
}: ModelDetailsBodyProps) {
	const { t } = useTranslation();
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const [tab, setTab] = useState<string | null>("overview");
	const hasLicenseOrTemplate = Boolean(details?.template || details?.license);
	// The launch-argument override is only read by the llama.cpp supervisor, so the Advanced tab is shown only for
	// llamacpp models — an Ollama/Codex/Azure entry would report success but the override would be silently ineffective
	// (and could bleed onto a same-named GGUF). Gated behind developer mode too.
	const showLaunchArgs = developerMode && model.provider === "llamacpp";

	// Fit content is computed here (not as a chained JSX ternary) to keep the panel readable: when model-fit is off
	// show a disabled note; otherwise mount the cache-only llmfit query only while the Fit tab is the active one.
	let fitContent = <Text c="dimmed">Model-fit recommendations are disabled on this node.</Text>;
	if (modelFitEnabled) {
		fitContent = tab === "fit" ? <ModelFitPanel modelName={model.modelName} /> : <span />;
	}

	return (
		<Tabs value={tab} onChange={setTab} keepMounted={false}>
			<Tabs.List>
				<Tabs.Tab value="overview">Overview</Tabs.Tab>
				<Tabs.Tab value="type">{t("pages.models.type.columnHeader", "Type")}</Tabs.Tab>
				<Tabs.Tab value="license">License &amp; template</Tabs.Tab>
				<Tabs.Tab value="fit">Fit</Tabs.Tab>
				{showLaunchArgs ? (
					<Tabs.Tab value="advanced" data-testid="model-advanced-tab">
						{t("pages.models.launchArgs.tab", "Advanced")}
					</Tabs.Tab>
				) : null}
			</Tabs.List>

			<Tabs.Panel value="overview" pt="md">
				<Stack gap="sm">
					{detailsLoading ? <Loader size="sm" /> : null}
					<Text>Parameter size: {model.parameterSizeLabel}</Text>
					<Text>Family: {model.familyLabel}</Text>
					<Text>Quantization: {model.quantizationLabel}</Text>
					<Text>
						{t("pages.models.local.origin.label", "Origin")}: {model.origin === "Imported"
							? t("pages.models.local.origin.imported", "Imported")
							: model.origin === "HuggingFace"
								? t("pages.models.local.origin.huggingFace", "Hugging Face")
								: t("pages.models.local.origin.legacy", "Legacy / unknown")}
					</Text>
					<Text>Context length: {details?.maxContextTokens?.toLocaleString() ?? "Unknown"}</Text>
					{details?.system ? <Alert color="blue">System prompt: {details.system}</Alert> : null}
				</Stack>
			</Tabs.Panel>

			<Tabs.Panel value="type" pt="md">
				<Stack gap="md">
					<Group gap="sm" align="center">
						<Badge color={kindBadgeColor(model.kind)} variant="light" data-testid={`model-kind-badge-${model.modelName}`}>
							{kindLabel(t, model.kind)}
						</Badge>
						{model.isOverridden ? (
							<Group gap={6} align="center">
								<Text size="sm" c="dimmed">
									Detected: {kindLabel(t, model.detectedKind)}
								</Text>
								<Button
									variant="subtle"
									size="xs"
									color="gray"
									leftSection={<IconArrowBackUp size={14} />}
									disabled={isActionPending}
									aria-label={`Reset ${model.modelName} type to detected`}
									onClick={() => onResetKind(model.modelName)}
								>
									{t("pages.models.type.reset", "Reset to detected")}
								</Button>
							</Group>
						) : null}
					</Group>

					{model.capabilities.length > 0 ? (
						<Group gap={4}>
							{model.capabilities.map((capability) => (
								<Badge key={capability} size="xs" variant="outline" color="gray">
									{capabilityLabel(t, capability)}
								</Badge>
							))}
						</Group>
					) : null}

					<Select
						label={t("pages.models.type.overrideLabel", "Override type")}
						aria-label={`Override type for ${model.modelName}`}
						data={buildKindOptions(t, model.kind)}
						value={model.kind}
						allowDeselect={false}
						disabled={isActionPending}
						w={220}
						onChange={(value) => {
							if (value && value !== model.kind) {
								onSetKind(model.modelName, value);
							}
						}}
					/>
				</Stack>
			</Tabs.Panel>

			<Tabs.Panel value="license" pt="md">
				<Stack gap="lg">
					{details?.template ? (
						<Stack gap={4}>
							<Text fw={600} size="sm">
								Template
							</Text>
							<Code block={true} style={{ whiteSpace: "pre-wrap" }} data-testid="model-template-content">
								{details.template}
							</Code>
						</Stack>
					) : null}
					{details?.license ? (
						<Stack gap={4}>
							<Text fw={600} size="sm">
								License
							</Text>
							<Code block={true} style={{ whiteSpace: "pre-wrap" }} data-testid="model-license-content">
								{details.license}
							</Code>
						</Stack>
					) : null}
					{!hasLicenseOrTemplate ? <Text c="dimmed">No license or template provided for this model.</Text> : null}
				</Stack>
			</Tabs.Panel>

			<Tabs.Panel value="fit" pt="md">
				{fitContent}
			</Tabs.Panel>

			{showLaunchArgs ? (
				<Tabs.Panel value="advanced" pt="md">
					{/* Mount the panel only while the Advanced tab is active so its override query fires on demand, mirroring Fit. */}
					{tab === "advanced" ? <ModelLaunchArgumentsPanel modelName={model.modelName} /> : <span />}
				</Tabs.Panel>
			) : null}
		</Tabs>
	);
}

// Per-model details dialog. Replaces the old side-by-side details card + standalone license/template modal: details,
// the editable type override, and llmfit fit info now live in one tabbed dialog opened by clicking a model name.
export function ModelDetailsDialog({
	opened,
	onClose,
	model,
	details,
	detailsLoading,
	isActionPending,
	modelFitEnabled,
	onSetKind,
	onResetKind,
}: ModelDetailsDialogProps) {
	return (
		<DialogShell opened={opened} onClose={onClose} title={model?.modelName ?? "Model details"} size="lg">
			{model ? (
				<ModelDetailsBody
					key={model.modelName}
					model={model}
					details={details}
					detailsLoading={detailsLoading}
					isActionPending={isActionPending}
					modelFitEnabled={modelFitEnabled}
					onSetKind={onSetKind}
					onResetKind={onResetKind}
				/>
			) : null}
		</DialogShell>
	);
}
