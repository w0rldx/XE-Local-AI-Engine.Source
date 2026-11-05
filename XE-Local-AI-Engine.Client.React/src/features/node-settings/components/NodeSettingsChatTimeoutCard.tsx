import { NumberInput, Text } from "@mantine/core";
import { IconMessage } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import type { NodeSettingsTimeoutInput } from "@/features/node-settings/models/NodeSettingsModel";

interface NodeSettingsChatTimeoutCardProps {
	readonly value: NodeSettingsTimeoutInput;
	readonly min: number;
	readonly max: number;
	readonly invalid: boolean;
	readonly onChange: (value: NodeSettingsTimeoutInput) => void;
}

export function NodeSettingsChatTimeoutCard({ value, min, max, invalid, onChange }: NodeSettingsChatTimeoutCardProps) {
	const { t } = useTranslation();
	return (
		<SectionCard title={t("pages.nodeSettings.localChatRuntime.title", "Local chat runtime")} icon={<IconMessage size={22} />}>
			<Text c="dimmed">
				{t(
					"pages.nodeSettings.localChatRuntime.description",
					"The maximum message request timeout bounds how long a single local chat message request (send or regenerate) may run before it is cancelled with a timeout. It is also reported to the platform via capability reports.",
				)}
			</Text>
			<NumberInput
				label={t("pages.nodeSettings.localChatRuntime.timeoutLabel", "Maximum message request timeout")}
				description={`${t("pages.nodeSettings.localChatRuntime.timeoutRange", "Allowed range: {{min}}–{{max}} seconds.", {
					min,
					max,
				})} ${t(
					"pages.nodeSettings.localChatRuntime.outputCapHint",
					"On slow hardware a long answer can reach this timeout before the answer length limit (Longest answer) stops it. Raise this timeout, or lower Longest answer.",
				)}`}
				suffix={` ${t("pages.nodeSettings.fields.seconds", "seconds")}`}
				min={min}
				max={max}
				clampBehavior="none"
				step={5}
				allowDecimal={false}
				value={value}
				onChange={onChange}
				error={
					invalid
						? t("pages.nodeSettings.localChatRuntime.timeoutError", "Enter a whole number from {{min}} to {{max}}.", {
								min,
								max,
							})
						: undefined
				}
			/>
		</SectionCard>
	);
}
