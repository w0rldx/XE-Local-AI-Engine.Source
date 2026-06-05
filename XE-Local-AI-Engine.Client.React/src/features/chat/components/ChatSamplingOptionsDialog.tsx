import { Button, Divider, NumberInput, Stack, Text, Textarea, Title } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { clampFieldMax, samplingFieldGroups, toWireSamplingOptions } from "@/features/chat/models/ChatSamplingOptions";
import { useChatSamplingPreferencesStore } from "@/features/chat/stores/ChatSamplingPreferencesStore";

interface ChatSamplingOptionsDialogProps {
	opened: boolean;
	onClose: () => void;
	// The active model's maximum context length, used to cap MaxOutputTokens and NumCtx inputs.
	maxContextTokens?: number;
}

export function ChatSamplingOptionsDialog({ opened, onClose, maxContextTokens }: ChatSamplingOptionsDialogProps) {
	const { t } = useTranslation();
	const options = useChatSamplingPreferencesStore((state) => state.options);
	const { setField, reset } = useChatSamplingPreferencesStore((state) => state.actions);

	// Derive whether any override is currently set to surface a reset affordance.
	const hasOverrides = toWireSamplingOptions(options) !== undefined;

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
								<NumberInput
									key={meta.key}
									label={t(meta.labelKey)}
									description={t(meta.descriptionKey)}
									min={meta.min}
									max={cappedMax}
									step={meta.step}
									decimalScale={meta.decimalScale}
									allowDecimal={meta.allowDecimal}
									value={numValue ?? ""}
									placeholder={t("pages.chat.samplingOptions.modelDefault", "Model default")}
									onChange={(val) => {
										// NumberInput.onChange emits `number | string` — string for partial input
										// (leading zeros, trailing decimal). Coerce to a real number; clear the
										// field on empty/non-finite so a string never enters the store or the wire.
										const num =
											typeof val === "number"
												? val
												: typeof val === "string" && val.trim() !== ""
													? Number(val)
													: Number.NaN;
										setField(meta.key, Number.isFinite(num) ? (num as never) : (undefined as never));
									}}
									data-testid={`chat-sampling-field-${meta.key}`}
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
