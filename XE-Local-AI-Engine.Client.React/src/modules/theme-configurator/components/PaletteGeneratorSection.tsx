import { ColorPicker, ColorSwatch, Input, Paper, Text, TextInput } from "@mantine/core";
import { useReducer, type ChangeEventHandler } from "react";

import type { PaletteGeneratorSectionProperties } from "@/modules/theme-configurator/components/ThemeConfigurator.types";
import { paletteIndexes, parseColor, tryParseColor } from "@/modules/theme-configurator/components/ColorUtils";

interface InvalidColorInput {
	baseColor: string;
	value: string;
}

interface InvalidColorInputAction {
	input: InvalidColorInput | null;
}

function invalidColorInputReducer(_state: InvalidColorInput | null, action: InvalidColorInputAction) {
	return action.input;
}

export function PaletteGeneratorSection({
	title,
	description,
	baseColorLabel,
	generatedScaleLabel,
	invalidColorLabel,
	baseColor,
	scale,
	onBaseColorChange,
}: PaletteGeneratorSectionProperties) {
	const [invalidColorInput, dispatchInvalidColorInput] = useReducer(invalidColorInputReducer, null);
	const activeInvalidInput = invalidColorInput?.baseColor === baseColor ? invalidColorInput.value : null;
	const inputValue = activeInvalidInput ?? baseColor;
	const invalidInput = activeInvalidInput !== null;

	const handleTextInputChange: ChangeEventHandler<HTMLInputElement> = (event) => {
		const nextValue = event.currentTarget.value;

		const parsedColor = tryParseColor(nextValue);
		if (!parsedColor) {
			dispatchInvalidColorInput({ input: { baseColor, value: nextValue } });
			return;
		}

		dispatchInvalidColorInput({ input: null });
		onBaseColorChange(parsedColor);
	};

	return (
		<Paper withBorder={true} radius="md" p="md">
			<div className="flex flex-col gap-3">
				<Text size="sm" fw={600}>
					{title}
				</Text>
				<Text size="xs" c="dimmed">
					{description}
				</Text>

				<TextInput
					label={baseColorLabel}
					value={inputValue}
					error={invalidInput ? invalidColorLabel : undefined}
					onChange={handleTextInputChange}
				/>

				<ColorPicker
					value={baseColor}
					format="hex"
					size="lg"
					onChange={(nextColor) => {
						const parsedColor = parseColor(nextColor, baseColor);
						dispatchInvalidColorInput({ input: null });
						onBaseColorChange(parsedColor);
					}}
				/>

				<Input.Label size="sm">{generatedScaleLabel}</Input.Label>
				<div className="grid grid-cols-2 sm:grid-cols-5 md:grid-cols-10 gap-3">
					{paletteIndexes.map((shadeIndex) => {
						const color = scale[shadeIndex] ?? baseColor;
						const isMainOrHoverShade = shadeIndex === 6 || shadeIndex === 7;

						return (
							<div
								key={`${title}-shade-${shadeIndex}`}
								className={`flex flex-col items-center gap-1 rounded border p-2 ${
									isMainOrHoverShade ? "border-gray-400" : "border-transparent"
								}`}
							>
								<ColorSwatch color={color} radius="sm" size={42} />
								<Text size="xs">{shadeIndex}</Text>
								<Text size="xs" className="font-mono uppercase text-center leading-none">
									{color}
								</Text>
							</div>
						);
					})}
				</div>
			</div>
		</Paper>
	);
}
