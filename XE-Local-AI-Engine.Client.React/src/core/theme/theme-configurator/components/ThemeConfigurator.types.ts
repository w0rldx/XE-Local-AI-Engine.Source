export type PaletteKey = "primary" | "secondary";

export interface PaletteGeneratorSectionProperties {
	title: string;
	description: string;
	baseColorLabel: string;
	generatedScaleLabel: string;
	invalidColorLabel: string;
	baseColor: string;
	scale: string[];
	onBaseColorChange: (_nextBaseColor: string) => void;
}
