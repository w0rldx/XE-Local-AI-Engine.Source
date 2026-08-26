import { describe, expect, it } from "vitest";

import { buildGeneratedScale } from "@/modules/theme-configurator/components/ColorUtils";

describe("buildGeneratedScale", () => {
	it.each([
		["#de0a1b", ["#ffe9eb", "#ffd1d4", "#fba0a7", "#f76c77", "#f5414e", "#f32634", "#f41726", "#de0a1b", "#c20016", "#aa0010"]],
		["#123456", ["#eef5fb", "#dce7f1", "#b4cce6", "#89b1db", "#679ad2", "#518bcd", "#4584cc", "#3771b4", "#2e64a2", "#123456"]],
		["#808080", ["#fff2f5", "#ece6e7", "#cfcdcd", "#b2b2b2", "#9a9a9a", "#8b8b8b", "#808080", "#737171", "#686465", "#5f5457"]],
	])("preserves Mantine's generated palette for %s", (baseColor, expected) => {
		expect(buildGeneratedScale(baseColor)).toEqual(expected);
	});
});
