import {
	ActionIcon,
	Alert,
	Button,
	FileInput,
	Group,
	NumberInput,
	Progress,
	SegmentedControl,
	Select,
	Stack,
	Switch,
	Text,
} from "@mantine/core";
import { IconRefresh } from "@tabler/icons-react";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { type AudioInputDevice, listAudioInputDevices } from "@/features/transcription/capture/CaptureSource";
import {
	type TranscriptionLanguageMode,
	type TranscriptionSourceKind,
	transcriptionDialogSourceKinds,
	transcriptionLanguageCodes,
} from "@/features/transcription/models/TranscriptionModels";
import {
	useCaptureProcesses,
	useTranscriptionModels,
	useTranscriptionRuntimeStatus,
} from "@/features/transcription/queries/useTranscriptionQueries";
import { useTranscriptionCaptureStore } from "@/features/transcription/stores/TranscriptionCaptureStore";

export interface NewTranscriptionSessionValues {
	readonly title: string;
	readonly sourceKind: TranscriptionSourceKind;
	readonly languageMode: TranscriptionLanguageMode;
	/** null while the language is auto-detected — the field is absent from the request rather than empty. */
	readonly languageOverride: string | null;
	readonly translate: boolean;
	readonly maxWindowSeconds: number;
	readonly channelAttribution: boolean;
	/** null for a live session: there is no upload, the audio arrives over the hub. */
	readonly file: File | null;
	/** The chosen microphone, or null for the browser default. Never sent to the node — capture is client-side. */
	readonly deviceId: string | null;
	/**
	 * The application to capture, for an ApplicationProcess session; null for every other source. Unlike the device
	 * id this one does reach the node — as the capture/process body once the session is live — so the create path
	 * has to remember it against the new session id.
	 */
	readonly processId: number | null;
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
// Chrome enumerates the operating-system default input under the literal deviceId "default" (and "communications"),
// so the sentinel for "let the browser choose" must be a value no browser can hand back: Mantine's Select throws on a
// duplicate option value, and a crashed dialog is how that collision surfaced in the fake-device E2E.
const DEFAULT_DEVICE = "xe:system-default";
// The node's own container sniffer decides; this only nudges the OS picker toward what it reads.
const AUDIO_ACCEPT = ".wav,.mp3,.flac,.ogg,.m4a,.mp4,.webm,.mkv,audio/*";

function usesMicrophone(sourceKind: TranscriptionSourceKind): boolean {
	return sourceKind === "Microphone" || sourceKind === "MicrophoneAndSystem";
}

function usesSystemAudio(sourceKind: TranscriptionSourceKind): boolean {
	return sourceKind === "SystemAudio" || sourceKind === "MicrophoneAndSystem";
}

function usesProcess(sourceKind: TranscriptionSourceKind): boolean {
	return sourceKind === "ApplicationProcess";
}

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
 * Starts a transcription session: a file upload, or live capture from the microphone, the system's audio output, or
 * both.
 *
 * A live session creates the row here and captures on the session view — the browser's screen-share picker has to open
 * inside the click that asks for it, so this dialog never starts capture itself.
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
	const lastSourceKind = useTranscriptionCaptureStore((state) => state.lastSourceKind);
	const setLastSourceKind = useTranscriptionCaptureStore((state) => state.actions.setLastSourceKind);
	const [selectedSourceKind, setSourceKind] = useState<TranscriptionSourceKind>(lastSourceKind);
	// The node reports whether it can capture one application at all; the option is absent where it cannot, rather
	// than present and refused. Windows 10 build 20348 and later is the whole of the support policy.
	const runtimeQuery = useTranscriptionRuntimeStatus();
	const processCaptureSupported = runtimeQuery.data?.processCaptureSupported === true;
	// A node ships with no weights, and the runtime only reports that on the first spawn — as a raw
	// "The selected transcription model is not installed." after the operator has already picked a file and waited
	// for the upload. The same cached catalogue the runtime card reads answers it before any of that, so the dialog
	// says it up front and names where to fix it. The submit stays enabled: the model can be fetched in another tab
	// while this session is being described, and a disabled button that explains nothing is what this replaces.
	const modelsQuery = useTranscriptionModels();
	const noModelInstalled = modelsQuery.data?.models.every((model) => !model.installed) === true;
	// The remembered kind is persisted; the option list is not. A box that stopped reporting support — and every
	// render before the runtime status has answered — would otherwise leave the SegmentedControl holding a value that
	// is not in its own `data`: nothing highlighted, an empty picker below it, and Create disabled with nothing on
	// screen saying why. Falling back to a kind that is always offered keeps the dialog usable.
	const sourceKind: TranscriptionSourceKind =
		!processCaptureSupported && selectedSourceKind === "ApplicationProcess" ? "File" : selectedSourceKind;
	// The microphone is remembered per SESSION, by the create path that knows the new session's id — not globally,
	// where a stale id would reach a later session that was configured for the system default.
	const [deviceId, setDeviceId] = useState(DEFAULT_DEVICE);
	const [devices, setDevices] = useState<readonly AudioInputDevice[]>([]);
	const [language, setLanguage] = useState(AUTO_LANGUAGE);
	const [translate, setTranslate] = useState(false);
	const [maxWindowSeconds, setMaxWindowSeconds] = useState(DEFAULT_MAX_WINDOW_SECONDS);
	const [channelAttribution, setChannelAttribution] = useState(false);
	const [file, setFile] = useState<File | null>(null);
	const [processId, setProcessId] = useState<number | null>(null);

	const languageChoices = useMemo(() => languageOptions(i18n.language), [i18n.language]);
	const isFileSource = sourceKind === "File";
	// Enumerated only while the dialog is open on this source: listing the box's audio sessions is work the node
	// should not do for a picker nobody is looking at.
	const processQuery = useCaptureProcesses(opened && usesProcess(sourceKind));
	const processes = processQuery.data ?? [];
	// Derived, not stored: an application the operator picked can exit before they submit, and a refresh then returns
	// a list it is no longer in. Keeping the id in state would leave a blank Select over a live pid and create the row
	// against a process nobody can capture.
	const chosenProcessId = processId !== null && processes.some((process) => process.pid === processId) ? processId : null;
	// A pid is the one thing this source cannot be started without, so submit waits for it rather than creating a row
	// the session view could never capture for.
	const canSubmit =
		(isFileSource ? file !== null : true) && (usesProcess(sourceKind) ? chosenProcessId !== null : true) && !isSubmitting;

	// Enumerated only while a microphone source is selected and the dialog is open: the list needs no permission, but
	// asking for it on a page that is not about to record is a device query the operator did not ask for.
	useEffect(() => {
		if (!opened || !usesMicrophone(sourceKind)) {
			return;
		}
		let cancelled = false;
		listAudioInputDevices()
			.then((found) => {
				if (!cancelled) {
					// One row per id: a browser that lists the same id twice would crash the Select on a duplicate value.
					setDevices([...new Map(found.map((device) => [device.deviceId, device])).values()]);
				}
			})
			.catch(() => undefined);
		return () => {
			cancelled = true;
		};
	}, [opened, sourceKind]);

	const close = (): void => {
		setSourceKind(lastSourceKind);
		setDeviceId(DEFAULT_DEVICE);
		setLanguage(AUTO_LANGUAGE);
		setTranslate(false);
		setMaxWindowSeconds(DEFAULT_MAX_WINDOW_SECONDS);
		setChannelAttribution(false);
		setFile(null);
		setProcessId(null);
		onClose();
	};

	const submit = (): void => {
		if (isFileSource && file === null) {
			return;
		}
		if (usesProcess(sourceKind) && chosenProcessId === null) {
			return;
		}
		const chosenDevice = deviceId === DEFAULT_DEVICE ? null : deviceId;
		// A file staged before the operator switched to a live source stays in state (the input is merely hidden); the
		// page routes on `file`, so it must be dropped here or the switch would silently upload the old recording.
		const stagedFile = isFileSource ? file : null;
		setLastSourceKind(sourceKind);
		onSubmit({
			// The upload endpoint never names the row, so the create call carries the title. This dialog offers no
			// title field: a file's own name is what the operator already chose, and a live session has no name to
			// borrow, so it is stamped with the time it started.
			title:
				stagedFile === null
					? t("pages.transcription.dialog.liveTitle", { time: new Date().toLocaleTimeString() })
					: stagedFile.name,
			sourceKind,
			languageMode: language === AUTO_LANGUAGE ? "auto" : "override",
			languageOverride: language === AUTO_LANGUAGE ? null : language,
			translate,
			maxWindowSeconds,
			channelAttribution,
			file: stagedFile,
			deviceId: chosenDevice,
			processId: usesProcess(sourceKind) ? chosenProcessId : null,
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
				{noModelInstalled ? (
					<Alert color="yellow" variant="light" data-testid="new-transcription-session-no-model">
						{t("pages.transcription.dialog.noModelInstalled")}
					</Alert>
				) : null}
				<Stack gap={4}>
					<Text size="sm" fw={500}>
						{t("pages.transcription.dialog.sourceLabel")}
					</Text>
					<SegmentedControl
						value={sourceKind}
						onChange={(value) => setSourceKind(value as TranscriptionSourceKind)}
						// The fifth option is absent, never disabled: per-application capture exists only on a recent
						// Windows build, and an option that can never be chosen on this box explains nothing.
						data={[...transcriptionDialogSourceKinds, ...(processCaptureSupported ? (["ApplicationProcess"] as const) : [])].map(
							(value) => ({
								value,
								label: t(`pages.transcription.source.${value}`),
							}),
						)}
						aria-label={t("pages.transcription.dialog.sourceLabel")}
						data-testid="new-transcription-session-source"
					/>
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
				{usesMicrophone(sourceKind) ? (
					<Select
						label={t("pages.transcription.dialog.deviceLabel")}
						// Labels stay empty until a microphone permission has been granted once, so the fallback names the
						// position in the list rather than rendering blank rows the operator cannot tell apart.
						description={
							devices.some((device) => device.label.length === 0) ? t("pages.transcription.dialog.deviceHint") : undefined
						}
						value={deviceId}
						onChange={(value) => setDeviceId(value ?? DEFAULT_DEVICE)}
						allowDeselect={false}
						data={[
							{ value: DEFAULT_DEVICE, label: t("pages.transcription.dialog.deviceDefault") },
							...devices.map((device, index) => ({
								value: device.deviceId,
								label:
									device.label.length > 0 ? device.label : t("pages.transcription.dialog.deviceUnnamed", { index: index + 1 }),
							})),
						]}
						data-testid="new-transcription-session-device"
					/>
				) : null}
				{usesProcess(sourceKind) ? (
					<Stack gap={4}>
						<Group gap="xs" align="flex-end" wrap="nowrap">
							<Select
								label={t("pages.transcription.dialog.processLabel")}
								placeholder={t("pages.transcription.dialog.processPlaceholder")}
								value={chosenProcessId === null ? null : String(chosenProcessId)}
								onChange={(value) => setProcessId(value === null ? null : Number(value))}
								allowDeselect={false}
								disabled={processes.length === 0}
								data={processes.map((process) => ({ value: String(process.pid), label: process.name }))}
								flex={1}
								data-testid="new-transcription-session-process"
							/>
							<ActionIcon
								variant="light"
								size="lg"
								loading={processQuery.isFetching}
								aria-label={t("pages.transcription.dialog.processRefresh")}
								onClick={() => {
									processQuery.refetch().catch(() => undefined);
								}}
								data-testid="new-transcription-session-process-refresh"
							>
								<IconRefresh size={16} />
							</ActionIcon>
						</Group>
						{processes.length === 0 && !processQuery.isFetching ? (
							<Text size="xs" c="dimmed" data-testid="new-transcription-session-process-empty">
								{t("pages.transcription.dialog.processEmpty")}
							</Text>
						) : null}
						<Text size="xs" c="dimmed" data-testid="new-transcription-session-process-hint">
							{t("pages.transcription.dialog.processDescription")}
						</Text>
					</Stack>
				) : null}
				{usesSystemAudio(sourceKind) ? (
					<Text size="xs" c="dimmed" data-testid="new-transcription-session-share-hint">
						{t("pages.transcription.dialog.shareHint")}
					</Text>
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
