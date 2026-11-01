import { NumberInput, SimpleGrid, Stack, Text, Textarea } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { type ChatSamplingOptions, samplingFieldGroups } from "@/core/runtime/ChatSamplingOptions";
import { directInferenceDefaultMaxOutputTokens } from "@/features/directInference/models/DirectInferenceModels";

interface DirectInferenceSamplingFieldsProps {
	value: ChatSamplingOptions;
	onChange: (value: ChatSamplingOptions) => void;
	disabled?: boolean;
}

// The chat sampling dialog's field table rendered inline as plain number inputs. A deliberate small copy rather than an
// import: the dialog belongs to the chat feature. The server refuses an out-of-range value, so the inputs clamp to the
// same ranges the table declares.
export function DirectInferenceSamplingFields({ value, onChange, disabled }: DirectInferenceSamplingFieldsProps) {
	const { t } = useTranslation();
	// The textarea keeps what was typed, empty lines included, so Enter starts a new line; only the value it reports is normalised.
	const [stopText, setStopText] = useState(() => (value.stop ?? []).join("\n"));

	return (
		<Stack gap="sm">
			{samplingFieldGroups.map((group) => (
				<Stack key={group.groupKey} gap={4}>
					<Text size="sm" fw={600}>
						{t(group.groupKey)}
					</Text>
					<SimpleGrid cols={{ base: 1, sm: 2, md: 4 }} spacing="sm">
						{group.fields.map((meta) => (
							<NumberInput
								key={meta.key}
								label={t(meta.labelKey)}
								min={meta.min}
								max={meta.max}
								step={meta.step}
								decimalScale={meta.decimalScale}
								allowDecimal={meta.allowDecimal}
								clampBehavior="strict"
								disabled={disabled}
								placeholder={
									meta.key === "maxOutputTokens"
										? t("pages.directInference.maxOutputTokensDefault", "Default: {{tokens}}", {
												tokens: directInferenceDefaultMaxOutputTokens.toLocaleString(),
											})
										: t("pages.chat.samplingOptions.modelDefault", "Model default")
								}
								value={(value[meta.key] as number | undefined) ?? ""}
								onChange={(next) => onChange({ ...value, [meta.key]: typeof next === "number" ? next : undefined })}
							/>
						))}
					</SimpleGrid>
				</Stack>
			))}
			<Textarea
				label={t("pages.chat.samplingOptions.stop", "Stop sequences")}
				autosize={true}
				minRows={1}
				disabled={disabled}
				placeholder={t("pages.chat.samplingOptions.modelDefault", "Model default")}
				value={stopText}
				onChange={(event) => {
					const raw = event.currentTarget.value;
					setStopText(raw);
					const stop = raw.split("\n").filter((line) => line.length > 0);
					onChange({ ...value, stop: stop.length > 0 ? stop : undefined });
				}}
			/>
		</Stack>
	);
}
