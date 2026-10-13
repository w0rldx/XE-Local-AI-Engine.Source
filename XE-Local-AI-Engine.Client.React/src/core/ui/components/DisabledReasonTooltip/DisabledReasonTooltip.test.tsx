// @vitest-environment jsdom

import { Button } from "@mantine/core";
import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { DisabledReasonTooltip } from "@/core/ui/components/DisabledReasonTooltip/DisabledReasonTooltip";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

function wrapperOf(testId: string): HTMLElement {
	const wrapper = screen.getByTestId(testId).parentElement;
	if (wrapper === null) {
		throw new Error("the control has no wrapper");
	}
	return wrapper;
}

describe("DisabledReasonTooltip", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("opens the reason on keyboard focus of the wrapper while the control is disabled", async () => {
		renderWithMantine(
			<DisabledReasonTooltip label="Save first" controlDisabled={true}>
				<Button disabled={true} data-testid="export">
					Export
				</Button>
			</DisabledReasonTooltip>,
		);

		const wrapper = wrapperOf("export");
		expect(wrapper.getAttribute("tabindex")).toBe("0");
		fireEvent.focus(wrapper);
		expect(await screen.findByRole("tooltip")).toHaveProperty("textContent", "Save first");
	});

	it("leaves the tab stop to an enabled control", () => {
		renderWithMantine(
			<DisabledReasonTooltip label="Save first" controlDisabled={false}>
				<Button data-testid="export">Export</Button>
			</DisabledReasonTooltip>,
		);

		expect(wrapperOf("export").hasAttribute("tabindex")).toBe(false);
	});

	it("adds no empty tab stop when the tooltip itself is disabled", () => {
		renderWithMantine(
			<DisabledReasonTooltip label="Save first" controlDisabled={true} disabled={true}>
				<Button disabled={true} data-testid="export">
					Export
				</Button>
			</DisabledReasonTooltip>,
		);

		expect(wrapperOf("export").hasAttribute("tabindex")).toBe(false);
	});
});
