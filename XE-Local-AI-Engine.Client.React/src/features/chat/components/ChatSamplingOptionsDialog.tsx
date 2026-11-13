import { ActionIcon, Button, Divider, Group, NumberInput, Slider, Stack, Text, Textarea, Title, Tooltip } from "@mantine/core";
import { IconRestore } from "@tabler/icons-react";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { chatErrorMessage } from "@/features/chat/models/ChatErrorMessage";
import type { ChatSamplingOptions, SamplingFieldMeta } from "@/features/chat/models/ChatSamplingOptions";
import { clampFieldMax, samplingFieldGroups, toWireSamplingOptions } from "@/features/chat/models/ChatSamplingOptions";
import { useChatSamplingPreferencesStore } from "@/features/chat/stores/ChatSamplingPreferencesStore";

// The open conversation's own settings (undefined: it follows this browser's defaults) and how to store them;
// onSave(undefined) clears them and rejects when the node refuses. Until the conversation's full read has loaded
// (loaded false) its stored settings are unknown, so nothing may be saved over them.
export interface ConversationSamplingTarget {
	options: ChatSamplingOptions | undefined;
	loaded: boolean;
	onSave: (options: ChatSamplingOptions | undefined) => Promise<void>;
}

// The node's limits for stop sequences (SetNodeChatConversationSamplingOptionsRequestValidator); blank lines are dropped.
const MAX_STOP_SEQUENCES = 16;
const MAX_STOP_SEQUENCE_LENGTH = 256;

interface ChatSamplingOptionsDialogProps {
	opened: boolean;
	onClose: () => void;
	// Set: the dialog edits this conversation's settings as a draft saved on Save. Unset: it edits this browser's
	// defaults, which new conversations and conversations without their own settings send.
	conversation?: ConversationSamplingTarget;
	// The active model's maximum context length, used to cap MaxOutputTokens and NumCtx inputs.
	maxContextTokens?: number;
	// True when the selected model is a cloud (Codex/Azure) one. Those requests never reach the local chat clients, so
	// the knobs marked localRuntimeOnly have no wire field there and are disabled rather than silently ignored.
	cloudModelSelected?: boolean;
}

interface SamplingFieldRowProps {
	meta: SamplingFieldMeta;
	numValue: number | undefined;
	cappedMax: number;
	unsupported: boolean;
	onCommit: (val: number | string) => void;
	onReset: () => void;
}

function SamplingFieldRow({ meta, numValue, cappedMax, unsupported, onCommit, onReset }: SamplingFieldRowProps) {
	const { t } = useTranslation();

	const resetLabel = t("pages.chat.samplingOptions.resetField", { field: t(meta.labelKey) });
	const unsupportedHint = t("pages.chat.samplingOptions.cloudUnsupported", "Not supported by cloud providers");

	const numberInput = (
		<NumberInput
			style={{ width: meta.slider ? 110 : undefined }}
			hideControls={meta.slider}
			label={meta.slider ? undefined : t(meta.labelKey)}
			description={meta.slider ? undefined : t(meta.descriptionKey)}
			disabled={unsupported}
			min={meta.min}
			max={cappedMax}
			step={meta.step}
			decimalScale={meta.decimalScale}
			allowDecimal={meta.allowDecimal}
			value={numValue ?? ""}
			placeholder={t("pages.chat.samplingOptions.modelDefault", "Model default")}
			onChange={onCommit}
			data-testid={`chat-sampling-field-${meta.key}`}
		/>
	);

	// Stays enabled even when the field is unsupported: a value stored while a local model was selected must remain
	// clearable without switching models first.
	const resetButton = (
		<Tooltip label={resetLabel} withArrow={true}>
			<ActionIcon
				variant="subtle"
				color="gray"
				size="sm"
				disabled={numValue === undefined}
				onClick={onReset}
				aria-label={resetLabel}
				data-testid={`chat-sampling-reset-${meta.key}`}
			>
				<IconRestore size={14} />
			</ActionIcon>
		</Tooltip>
	);

	if (meta.slider) {
		return (
			<Stack gap={4}>
				<Group justify="space-between" wrap="nowrap">
					<Text size="sm" fw={500}>
						{t(meta.labelKey)}
					</Text>
					{resetButton}
				</Group>
				<Text size="xs" c="dimmed">
					{t(meta.descriptionKey)}
				</Text>
				{unsupported ? (
					<Text size="xs" c="dimmed" fs="italic" data-testid={`chat-sampling-unsupported-${meta.key}`}>
						{unsupportedHint}
					</Text>
				) : null}
				<Group wrap="nowrap" align="center" gap="sm">
					<Slider
						style={{ flex: 1 }}
						disabled={unsupported}
						min={meta.min}
						max={cappedMax}
						step={meta.step}
						value={numValue ?? meta.min}
						label={(v) => String(v)}
						marks={[
							{ value: meta.min, label: String(meta.min) },
							{ value: cappedMax, label: String(cappedMax) },
						]}
						onChange={onCommit}
						data-testid={`chat-sampling-slider-${meta.key}`}
					/>
					{numberInput}
				</Group>
			</Stack>
		);
	}

	// Non-slider field (seed): label+description inside NumberInput, reset beside it.
	return (
		<Group wrap="nowrap" align="flex-end" gap="xs">
			<div style={{ flex: 1 }}>{numberInput}</div>
			{resetButton}
		</Group>
	);
}

export function ChatSamplingOptionsDialog({
	opened,
	onClose,
	conversation,
	maxContextTokens,
	cloudModelSelected = false,
}: ChatSamplingOptionsDialogProps) {
	const { t } = useTranslation();
	const defaults = useChatSamplingPreferencesStore((state) => state.options);
	const { setField: setDefaultField, reset } = useChatSamplingPreferencesStore((state) => state.actions);
	// The conversation draft: undefined until the first edit, so a reopened dialog shows the stored settings again.
	const [draft, setDraft] = useState<ChatSamplingOptions>();
	const [saving, setSaving] = useState(false);
	const [saveError, setSaveError] = useState<string>();
	const options = conversation ? (draft ?? conversation.options ?? defaults) : defaults;
	const stop = options.stop ?? [];
	const stopOverLimit = stop.length > MAX_STOP_SEQUENCES || stop.some((sequence) => sequence.length > MAX_STOP_SEQUENCE_LENGTH);
	const saveBlocked = conversation?.loaded !== true || saving;
	const setField = <K extends keyof ChatSamplingOptions>(key: K, value: ChatSamplingOptions[K]): void => {
		if (conversation) {
			// A draft begun before the stored settings loaded would carry this browser's defaults into them.
			if (conversation.loaded) {
				setDraft({ ...options, [key]: value });
			}
		} else {
			setDefaultField(key, value);
		}
	};
	const close = (): void => {
		if (saving) {
			return;
		}
		setDraft(undefined);
		setSaveError(undefined);
		onClose();
	};
	// Stays open until the node answers, so a refusal is shown here and the draft is kept for another try.
	const save = async (next: ChatSamplingOptions | undefined): Promise<void> => {
		if (!conversation) {
			return;
		}
		setSaving(true);
		setSaveError(undefined);
		try {
			await conversation.onSave(next);
		} catch (error) {
			setSaveError(chatErrorMessage(error));
			return;
		} finally {
			setSaving(false);
		}
		setDraft(undefined);
		onClose();
	};

	// Derive whether any override is currently set to surface a reset affordance.
	const hasOverrides = toWireSamplingOptions(options) !== undefined;

	// Mantine's controlled NumberInput/Slider can emit a spurious onChange during initial mount (observed: the
	// num_ctx field committing its min on open). Ignore commits until after mount so merely opening the dialog never
	// writes to the store — preserving the byte-identical "no override" invariant. User edits fire well after mount.
	const ready = useRef(false);
	useEffect(() => {
		ready.current = true;
		return () => {
			ready.current = false;
		};
	}, []);

	// Shared coercion: slider gives number directly; NumberInput gives number | string.
	// Empty/blank/NaN -> undefined (clear the field); finite number -> set.
	function commitField(key: SamplingFieldMeta["key"], val: number | string): void {
		if (!ready.current) {
			return;
		}

		const num = typeof val === "number" ? val : typeof val === "string" && val.trim() !== "" ? Number(val) : Number.NaN;
		setField(key, Number.isFinite(num) ? (num as never) : (undefined as never));
	}

	const footer = conversation ? (
		<>
			<Button
				variant="subtle"
				color="gray"
				onClick={() => save(undefined)}
				disabled={saveBlocked || conversation.options === undefined}
				data-testid="chat-sampling-use-defaults-button"
			>
				{t("pages.chat.samplingOptions.useDefaults", "Use defaults")}
			</Button>
			<Button variant="default" onClick={close} disabled={saving} data-testid="chat-sampling-cancel-button">
				{t("common.cancel", "Cancel")}
			</Button>
			<Button
				onClick={() => save(draft)}
				disabled={saveBlocked || draft === undefined || stopOverLimit}
				loading={saving}
				data-testid="chat-sampling-save-button"
			>
				{t("common.save", "Save")}
			</Button>
		</>
	) : (
		<>
			<Button variant="subtle" color="gray" onClick={reset} disabled={!hasOverrides} data-testid="chat-sampling-reset-button">
				{t("pages.chat.samplingOptions.reset", "Reset to defaults")}
			</Button>
			<Button onClick={close} data-testid="chat-sampling-close-button">
				{t("pages.chat.samplingOptions.close", "Close")}
			</Button>
		</>
	);

	return (
		<DialogShell
			opened={opened}
			onClose={close}
			title={t("pages.chat.samplingOptions.title", "Advanced sampling options")}
			size="min(42rem, 95vw)"
			footer={footer}
		>
			<Stack gap="lg" py="sm">
				<Text size="sm" c="dimmed" data-testid="chat-sampling-description">
					{conversation
						? t(
								"pages.chat.samplingOptions.conversationDescription",
								"These settings are saved with this conversation, so they follow it to every device. Use defaults returns it to this browser's defaults.",
							)
						: t(
								"pages.chat.samplingOptions.description",
								"These defaults override the model defaults for new conversations and for conversations without their own settings. They are stored in this browser only.",
							)}
				</Text>
				{saveError ? (
					<Text size="sm" c="red" role="alert" data-testid="chat-sampling-save-error">
						{saveError}
					</Text>
				) : null}

				{samplingFieldGroups.map((group, groupIdx) => (
					<Stack key={group.groupKey} gap="md">
						{groupIdx > 0 ? <Divider /> : null}
						<Title order={2} size="h5">
							{t(group.groupKey)}
						</Title>

						{group.fields.map((meta) => {
							const rawValue = options[meta.key];
							const numValue = typeof rawValue === "number" ? rawValue : undefined;
							const cappedMax = clampFieldMax(meta, maxContextTokens);

							return (
								<SamplingFieldRow
									key={meta.key}
									meta={meta}
									numValue={numValue}
									cappedMax={cappedMax}
									unsupported={cloudModelSelected && meta.localRuntimeOnly === true}
									onCommit={(val) => commitField(meta.key, val)}
									onReset={() => setField(meta.key, undefined as never)}
								/>
							);
						})}
					</Stack>
				))}

				{/* Stop sequences: one entry per line */}
				<Textarea
					label={t("pages.chat.samplingOptions.stop", "Stop sequences")}
					description={t(
						"pages.chat.samplingOptions.stopDescription",
						"One stop sequence per line. The model halts when it emits any of these strings.",
					)}
					placeholder={t("pages.chat.samplingOptions.modelDefault", "Model default")}
					value={stop.join("\n")}
					error={
						stopOverLimit
							? t("pages.chat.samplingOptions.stopLimit", "At most {{count}} stop sequences, each up to {{length}} characters.", {
									count: MAX_STOP_SEQUENCES,
									length: MAX_STOP_SEQUENCE_LENGTH,
								})
							: undefined
					}
					onChange={(e) => {
						const sequences: string[] = [];
						for (const l of e.currentTarget.value.split("\n")) {
							const trimmed = l.trimEnd();
							if (trimmed.length > 0) {
								sequences.push(trimmed);
							}
						}
						setField("stop", sequences.length > 0 ? sequences : undefined);
					}}
					autosize={true}
					minRows={2}
					maxRows={6}
					data-testid="chat-sampling-field-stop"
				/>
			</Stack>
		</DialogShell>
	);
}
