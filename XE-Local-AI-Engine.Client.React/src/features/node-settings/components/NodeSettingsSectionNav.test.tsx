// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { NodeSettingsSectionNav } from "@/features/node-settings/components/NodeSettingsSectionNav";
import type { NodeSettingsSectionId } from "@/features/node-settings/models/NodeSettingsSections";
import { renderWithProviders } from "@/test/RenderWithProviders";

describe("NodeSettingsSectionNav", () => {
	afterEach(() => {
		cleanup();
	});

	// On a phone, and on a phone in landscape where a sticky list could not scroll past the save bar, the sections
	// collapse into one select instead of a ~100px column.
	it.each([
		[true, 1, 0],
		[false, 0, 2],
	])("compact=%s renders %i select(s) and %i section button(s)", (compact, selects, buttons) => {
		renderWithProviders(
			<NodeSettingsSectionNav
				compact={compact}
				sections={["general", "chat"]}
				active="general"
				onSelect={vi.fn()}
				dirtyCounts={new Map<NodeSettingsSectionId, number>()}
			/>,
		);

		expect(screen.queryAllByTestId("node-settings-section-select")).toHaveLength(selects);
		expect(screen.queryAllByTestId(/^node-settings-section-(general|chat)$/)).toHaveLength(buttons);
	});
});
