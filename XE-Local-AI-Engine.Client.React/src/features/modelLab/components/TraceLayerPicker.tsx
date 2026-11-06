import { Button, Group, MultiSelect, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { defaultTraceLayers } from "@/features/modelLab/models/ModelLabArchitecture";

interface TraceLayerPickerProps {
	// The checkpoint's layer count from the check; the MTP block has no layer there, so it is never offered.
	blockCount: number;
	value: number[];
	onChange: (layers: number[]) => void;
	disabled: boolean;
}

// Which blocks a trace records. Nothing chosen sends nothing, and the server traces its three quarter points.
export function TraceLayerPicker({ blockCount, value, onChange, disabled }: TraceLayerPickerProps) {
	const { t } = useTranslation();
	const options = Array.from({ length: blockCount }, (_, index) => ({
		value: String(index),
		label: t("pages.modelLab.trace.layerOption", "Block {{index}}", { index }),
	}));
	return (
		<Stack gap={4}>
			<MultiSelect
				label={t("pages.modelLab.trace.layers", "Blocks to trace")}
				description={
					value.length === 0
						? t("pages.modelLab.trace.layersDefault", "None chosen: blocks {{layers}} are traced.", {
								layers: defaultTraceLayers(blockCount).join(", "),
							})
						: undefined
				}
				data={options}
				value={value.map(String)}
				onChange={(chosen) => onChange(chosen.map(Number).sort((left, right) => left - right))}
				searchable={true}
				disabled={disabled}
				data-testid="model-lab-trace-layers"
			/>
			<Group gap="xs">
				<Button
					variant="subtle"
					size="compact-xs"
					disabled={disabled || value.length === blockCount}
					onClick={() => onChange(options.map((_, index) => index))}
					data-testid="model-lab-trace-layers-all"
				>
					{t("pages.modelLab.trace.layersAll", "All blocks")}
				</Button>
				<Button
					variant="subtle"
					size="compact-xs"
					disabled={disabled || value.length === 0}
					onClick={() => onChange([])}
					data-testid="model-lab-trace-layers-clear"
				>
					{t("pages.modelLab.trace.layersClear", "Clear")}
				</Button>
			</Group>
			<Text size="xs" c="dimmed">
				{t(
					"pages.modelLab.trace.layersCost",
					"Each traced block adds generation time: on tested models three blocks added about 9 % and every block about 60 %.",
				)}
			</Text>
		</Stack>
	);
}
