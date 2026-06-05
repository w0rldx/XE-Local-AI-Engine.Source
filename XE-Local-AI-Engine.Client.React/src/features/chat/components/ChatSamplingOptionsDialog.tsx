import { ActionIcon, Button, Divider, Group, NumberInput, Slider, Stack, Text, Textarea, Title, Tooltip } from "@mantine/core";
import { IconRestore } from "@tabler/icons-react";
import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import type { SamplingFieldMeta } from "@/features/chat/models/ChatSamplingOptions";
import { clampFieldMax, samplingFieldGroups, toWireSamplingOptions } from "@/features/chat/models/ChatSamplingOptions";
import { useChatSamplingPreferencesStore } from "@/features/chat/stores/ChatSamplingPreferencesStore";

interface ChatSamplingOptionsDialogProps {
	opened: boolean;
	onClose: () => void;
	// The active model's maximum context length, used to cap MaxOutputTokens and NumCtx inputs.
	maxContextTokens?: number;
}

interface SamplingFieldRowProps {
	meta: SamplingFieldMeta;
	numValue: number | undefined;
	cappedMax: number;
	onCommit: (val: number | string) => void;
	onReset: () => void;
}

function SamplingFieldRow({ meta, numValue, cappedMax, onCommit, onReset }: SamplingFieldRowProps) {
	const { t } = useTranslation();

	const resetLabel = t("pages.chat.samplingOptions.resetField", { field: t(meta.labelKey) });

	const numberInput = (
		<NumberInput
			style={{ width: meta.slider ? 110 : undefined }}
			hideControls={meta.slider}
			label={meta.slider ? undefined : t(meta.labelKey)}
			description={meta.slider ? undefined : t(meta.descriptionKey)}
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
				<Group wrap="nowrap" align="center" gap="sm">
					<Slider
						style={{ flex: 1 }}
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

export function ChatSamplingOptionsDialog({ opened, onClose, maxContextTokens }: ChatSamplingOptionsDialogProps) {
	const { t } = useTranslation();
	const options = useChatSamplingPreferencesStore((state) => state.options);
	const { setField, reset } = useChatSamplingPreferencesStore((state) => state.actions);

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

		const num =
			typeof val === "number"
				? val
				: typeof val === "string" && val.trim() !== ""
					? Number(val)
					: Number.NaN;
		setField(key, Number.isFinite(num) ? (num as never) : (undefined as never));
	}

	const footer = (
		<>
			<Button variant="subtle" color="gray" onClick={reset} disabled={!hasOverrides} data-testid="chat-sampling-reset-button">
				{t("pages.chat.samplingOptions.reset", "Reset to defaults")}
			</Button>
			<Button onClick={onClose} data-testid="chat-sampling-close-button">
				{t("pages.chat.samplingOptions.close", "Close")}
			</Button>
		</>
	);

	return (
		<DialogShell
			opened={opened}
			onClose={onClose}
			title={t("pages.chat.samplingOptions.title", "Advanced sampling options")}
			size="min(42rem, 95vw)"
			footer={footer}
		>
			<Stack gap="lg" py="sm">
				<Text size="sm" c="dimmed">
					{t(
						"pages.chat.samplingOptions.description",
						"These parameters override the model defaults for the next message send. They are stored in this browser only and are not persisted per-conversation.",
					)}
				</Text>

				{samplingFieldGroups.map((group, groupIdx) => (
					<Stack key={group.groupKey} gap="md">
						{groupIdx > 0 ? <Divider /> : null}
						<Title order={5}>{t(group.groupKey)}</Title>

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
					description={t("pages.chat.samplingOptions.stopDescription", "One stop sequence per line. The model halts when it emits any of these strings.")}
					placeholder={t("pages.chat.samplingOptions.modelDefault", "Model default")}
					value={(options.stop ?? []).join("\n")}
					onChange={(e) => {
						const lines = e.currentTarget.value.split("\n");
						const sequences = lines.map((l) => l.trimEnd()).filter((l) => l.length > 0);
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
