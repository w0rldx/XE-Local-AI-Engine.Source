import { Alert, Badge, Button, Group, List, Select, Stack, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconArrowLeft, IconFileImport } from "@tabler/icons-react";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientEndpointsModelFitV1PreviewGgufImportResponse } from "@/core/api/generated";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { formatModelSize } from "@/features/models/models/LocalModelModel";
import { importErrorCodeFrom, importErrorMessage } from "@/features/models/models/GgufImportErrors";
import { usePreviewGgufImport, useStartGgufImport } from "@/features/models/queries/useGgufAcquisitions";

interface GgufImportDialogProps {
	readonly opened: boolean;
	readonly onClose: () => void;
	readonly onStarted: (operationId: string) => void;
}

export function GgufImportDialog({ opened, onClose, onStarted }: GgufImportDialogProps) {
	const { t } = useTranslation();
	const [sourcePath, setSourcePath] = useState("");
	const [preview, setPreview] = useState<XeLocalAiEngineClientEndpointsModelFitV1PreviewGgufImportResponse>();
	const [modelBaseName, setModelBaseName] = useState("");
	const [quantization, setQuantization] = useState<string | null>(null);
	const previewMutation = usePreviewGgufImport();
	const startMutation = useStartGgufImport();
	const trimmedSourcePath = sourcePath.trim();

	useEffect(() => {
		if (!opened) {
			setSourcePath("");
			setPreview(undefined);
			setModelBaseName("");
			setQuantization(null);
			previewMutation.reset();
			startMutation.reset();
		}
	}, [opened, previewMutation.reset, startMutation.reset]);

	const previewErrorCode = importErrorCodeFrom(previewMutation.error);
	const startErrorCode = importErrorCodeFrom(startMutation.error);
	const resultingModelName = useMemo(() => {
		if (!preview || !quantization) {
			return "";
		}
		if (modelBaseName === preview.modelBaseName && quantization === preview.detectedQuantization && preview.canonicalModelName) {
			return preview.canonicalModelName;
		}
		return `${modelBaseName.trim()}:${quantization}`;
	}, [modelBaseName, preview, quantization]);

	const runPreview = (): void => {
		previewMutation.mutate(trimmedSourcePath, {
			onSuccess: (response) => {
				if (!response) {
					return;
				}
				setPreview(response);
				setModelBaseName(response.modelBaseName);
				setQuantization(
					response.detectedQuantization ?? response.canonicalQuantizationChoices[0] ?? null,
				);
			},
		});
	};

	const startImport = (): void => {
		if (!preview || !quantization || !modelBaseName.trim()) {
			return;
		}
		startMutation.mutate(
			{ sourcePath: trimmedSourcePath, previewToken: preview.previewToken, modelBaseName: modelBaseName.trim(), quantization },
			{
				onSuccess: (ticket) => {
					if (ticket?.operationId) {
						onStarted(ticket.operationId);
						onClose();
					}
				},
			},
		);
	};

	return (
		<DialogShell
			opened={opened}
			onClose={onClose}
			title={t("pages.models.gguf.import.dialogTitle", "Import GGUF model")}
			data-testid="gguf-import-dialog"
			confirmCloseWhen={startMutation.isPending}
		>
			{preview ? (
				<Stack gap="md">
					<Group justify="space-between">
						<Text fw={600}>{preview.sourceDisplayName}</Text>
						<Badge variant="light">{formatModelSize(preview.sizeBytes)}</Badge>
					</Group>
					<Text size="sm" c="dimmed">
						{t("pages.models.gguf.import.metadata", "GGUF v{{version}} · {{architecture}}", {
							version: preview.ggufVersion ?? "?",
							architecture: preview.architecture ?? t("common.unknown", "Unknown"),
						})}
					</Text>
					<TextInput
						label={t("pages.models.gguf.import.modelBaseName", "Model base name")}
						value={modelBaseName}
						onChange={(event) => setModelBaseName(event.currentTarget.value)}
						required={true}
					/>
					<Select
						label={t("pages.models.gguf.import.quantization", "Quantization")}
						data={preview.canonicalQuantizationChoices}
						value={quantization}
						onChange={setQuantization}
						allowDeselect={false}
						required={true}
					/>
					<Text size="sm" aria-live="polite">
						{t("pages.models.gguf.import.resultingName", "Installed model: {{name}}", { name: resultingModelName })}
					</Text>
					{preview.hasSufficientStorage === false ? (
						<Alert color="red" icon={<IconAlertTriangle size={16} />}>
							{t("pages.models.gguf.import.storageInsufficient", "There is not enough free storage for this import.")}
						</Alert>
					) : null}
					{preview.hasSufficientStorage == null ? (
						<Alert color="yellow">{t("pages.models.gguf.import.storageUnknown", "Available storage could not be verified.")}</Alert>
					) : null}
					{preview.warnings.length > 0 ? (
						<Alert color="yellow" title={t("pages.models.gguf.import.warnings", "Warnings")}>
							<List size="sm">
								{preview.warnings.map((warning) => <List.Item key={warning}>{warning}</List.Item>)}
							</List>
						</Alert>
					) : null}
					{startMutation.error ? <Alert color="red">{importErrorMessage(t, startErrorCode)}</Alert> : null}
					<Group justify="space-between">
						<Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={() => setPreview(undefined)}>
							{t("common.back", "Back")}
						</Button>
						<Button
							leftSection={<IconFileImport size={16} />}
							onClick={startImport}
							loading={startMutation.isPending}
							disabled={!quantization || !modelBaseName.trim() || preview.hasSufficientStorage === false}
						>
							{t("pages.models.gguf.import.confirm", "Import model")}
						</Button>
					</Group>
				</Stack>
			) : (
				<Stack gap="md">
					<Text>{t("pages.models.gguf.import.pathHelp", "Enter the absolute path to a standalone GGUF file on this computer.")}</Text>
					<TextInput
						label={t("pages.models.gguf.import.sourcePath", "GGUF file path")}
						placeholder={t("pages.models.gguf.import.pathPlaceholder", "/path/to/model.gguf")}
						value={sourcePath}
						onChange={(event) => setSourcePath(event.currentTarget.value)}
						required={true}
						autoFocus={true}
					/>
					{previewMutation.error ? <Alert color="red">{importErrorMessage(t, previewErrorCode)}</Alert> : null}
					<Group justify="flex-end">
						<Button onClick={runPreview} loading={previewMutation.isPending} disabled={!trimmedSourcePath}>
							{t("pages.models.gguf.import.preview", "Preview import")}
						</Button>
					</Group>
				</Stack>
			)}
		</DialogShell>
	);
}
