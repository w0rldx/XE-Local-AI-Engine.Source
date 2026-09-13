import { Button, FileInput, Group, NumberInput, Progress, SegmentedControl, Select, Stack, Switch, Text } from "@mantine/core";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import {
	type TranscriptionLanguageMode,
	type TranscriptionSourceKind,
	transcriptionDialogSourceKinds,
	transcriptionLanguageCodes,
} from "@/features/transcription/models/TranscriptionModels";

export interface NewTranscriptionSessionValues {
	readonly title: string;
	readonly sourceKind: TranscriptionSourceKind;
	readonly languageMode: TranscriptionLanguageMode;
	/** null while the language is auto-detected — the field is absent from the request rather than empty. */
	readonly languageOverride: string | null;
	readonly translate: boolean;
	readonly maxWindowSeconds: number;
	readonly channelAttribution: boolean;
	readonly file: File;
}

interface NewTranscriptionSessionDialogProps {
	readonly opened: boolean;
	readonly isSubmitting: boolean;
	/** 0..100 while the staged recording is being sent. Rendered only while a submit is in flight. */
	readonly uploadProgressPercent: number;
	readonly errorMessage?: string;
	readonly onClose: () => void;
	readonly onSubmit: (values: NewTranscriptionSessionValues) => void;
}

const DEFAULT_MAX_WINDOW_SECONDS = 5;
const AUTO_LANGUAGE = "auto";
// The node's own container sniffer decides; this only nudges the OS picker toward what it reads.
const AUDIO_ACCEPT = ".wav,.mp3,.flac,.ogg,.m4a,.mp4,.webm,.mkv,audio/*";

/**
 * Names the offered languages through the platform's own table, so nine language names do not become nine
 * translation keys per locale. Constructing an Intl.DisplayNames is not cheap and the table is per locale, not per
 * render, so one instance is built per locale and reused. A runtime without the table yields the upper-cased codes.
 */
function languageOptions(locale: string): readonly { value: string; label: string }[] {
	let displayNames: Intl.DisplayNames | null = null;
	try {
		displayNames = new Intl.DisplayNames([locale], { type: "language" });
	} catch {
		displayNames = null;
	}
	return transcriptionLanguageCodes.map((code) => ({
		value: code,
		label: displayNames?.of(code) ?? code.toUpperCase(),
	}));
}

/**
 * Starts a transcription session.
 *
 * All four source kinds are rendered, but only File is reachable in this slice: live capture arrives with the capture
 * pipeline, and showing the others disabled is what makes that slice a data change (drop `disabled`, add the capture
 * branch) rather than a layout change. The max-window and channel-attribution controls are conditioned on the source
 * for the same reason, which means both are inert while File is the only reachable option.
 */
export function NewTranscriptionSessionDialog({
	opened,
	isSubmitting,
	uploadProgressPercent,
	errorMessage,
	onClose,
	onSubmit,
}: NewTranscriptionSessionDialogProps) {
	const { t, i18n } = useTranslation();
	const [sourceKind, setSourceKind] = useState<TranscriptionSourceKind>("File");
	const [language, setLanguage] = useState(AUTO_LANGUAGE);
	const [translate, setTranslate] = useState(false);
	const [maxWindowSeconds, setMaxWindowSeconds] = useState(DEFAULT_MAX_WINDOW_SECONDS);
	const [channelAttribution, setChannelAttribution] = useState(false);
	const [file, setFile] = useState<File | null>(null);

	const languageChoices = useMemo(() => languageOptions(i18n.language), [i18n.language]);
	const isFileSource = sourceKind === "File";
	const canSubmit = isFileSource && file !== null && !isSubmitting;

	const close = (): void => {
		setSourceKind("File");
		setLanguage(AUTO_LANGUAGE);
		setTranslate(false);
		setMaxWindowSeconds(DEFAULT_MAX_WINDOW_SECONDS);
		setChannelAttribution(false);
		setFile(null);
		onClose();
	};

	const submit = (): void => {
		if (file === null) {
			return;
		}
		onSubmit({
			// The upload endpoint never names the row, so the create call carries the title. This dialog offers no
			// title field: the file's own name is what the operator already chose, and typing it twice adds nothing.
			title: file.name,
			sourceKind,
			languageMode: language === AUTO_LANGUAGE ? "auto" : "override",
			languageOverride: language === AUTO_LANGUAGE ? null : language,
			translate,
			maxWindowSeconds,
			channelAttribution,
			file,
		});
	};

	return (
		<DialogShell
			opened={opened}
			onClose={close}
			title={t("pages.transcription.dialog.title")}
			data-testid="new-transcription-session-dialog"
			// A staged file is unsaved work: a stray overlay click must not discard the operator's pick.
			confirmCloseWhen={file !== null}
			footer={
				<Group justify="flex-end">
					<Button variant="subtle" onClick={close} data-testid="new-transcription-session-cancel">
						{t("common.cancel")}
					</Button>
					<Button onClick={submit} disabled={!canSubmit} loading={isSubmitting} data-testid="new-transcription-session-submit">
						{t("pages.transcription.dialog.submit")}
					</Button>
				</Group>
			}
		>
			<Stack gap="md">
				{errorMessage ? (
					<InlineErrorAlert message={errorMessage} variant="light" data-testid="new-transcription-session-error" />
				) : null}
				<Stack gap={4}>
					<Text size="sm" fw={500}>
						{t("pages.transcription.dialog.sourceLabel")}
					</Text>
					<SegmentedControl
						value={sourceKind}
						onChange={(value) => setSourceKind(value as TranscriptionSourceKind)}
						data={transcriptionDialogSourceKinds.map((value) => ({
							value,
							label: t(`pages.transcription.source.${value}`),
							disabled: value !== "File",
						}))}
						aria-label={t("pages.transcription.dialog.sourceLabel")}
						data-testid="new-transcription-session-source"
					/>
					<Text size="xs" c="dimmed">
						{t("pages.transcription.dialog.sourceComingSoon")}
					</Text>
				</Stack>
				{isFileSource ? (
					<FileInput
						value={file}
						onChange={setFile}
						accept={AUDIO_ACCEPT}
						clearable={true}
						label={t("pages.transcription.dialog.fileLabel")}
						description={t("pages.transcription.dialog.fileDescription")}
						placeholder={t("pages.transcription.dialog.filePlaceholder")}
						data-testid="new-transcription-session-file"
					/>
				) : null}
				<Select
					label={t("pages.transcription.dialog.languageLabel")}
					value={language}
					onChange={(value) => setLanguage(value ?? AUTO_LANGUAGE)}
					allowDeselect={false}
					data={[{ value: AUTO_LANGUAGE, label: t("pages.transcription.dialog.languageAuto") }, ...languageChoices]}
					data-testid="new-transcription-session-language"
				/>
				<Switch
					checked={translate}
					onChange={(event) => setTranslate(event.currentTarget.checked)}
					label={t("pages.transcription.dialog.translateLabel")}
					description={t("pages.transcription.dialog.translateDescription")}
					data-testid="new-transcription-session-translate"
				/>
				{isFileSource ? null : (
					<NumberInput
						value={maxWindowSeconds}
						onChange={(value) => setMaxWindowSeconds(typeof value === "number" ? value : DEFAULT_MAX_WINDOW_SECONDS)}
						min={2}
						max={10}
						label={t("pages.transcription.dialog.maxWindowLabel")}
						description={t("pages.transcription.dialog.maxWindowDescription")}
						data-testid="new-transcription-session-max-window"
					/>
				)}
				{isSubmitting ? (
					<Progress
						value={uploadProgressPercent}
						aria-label={t("pages.transcription.dialog.uploadProgress")}
						data-testid="new-transcription-session-progress"
					/>
				) : null}
				<Switch
					checked={channelAttribution}
					onChange={(event) => setChannelAttribution(event.currentTarget.checked)}
					disabled={sourceKind !== "MicrophoneAndSystem"}
					label={t("pages.transcription.dialog.channelAttributionLabel")}
					description={t("pages.transcription.dialog.channelAttributionDescription")}
					data-testid="new-transcription-session-channel-attribution"
				/>
			</Stack>
		</DialogShell>
	);
}
