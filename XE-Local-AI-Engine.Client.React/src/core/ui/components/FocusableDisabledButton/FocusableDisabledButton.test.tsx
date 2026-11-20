// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { FocusableDisabledButton } from "@/core/ui/components/FocusableDisabledButton/FocusableDisabledButton";
import { installJsdomEnvironmentMocks, renderWithMantine, testMantineTheme } from "@/test/MantineTestRender";

// jsdom does not turn Enter or Space into a click; a browser does, and that click is what the guard swallows. Each
// key is sent anyway (down, up, then the click a browser would dispatch) so a key handler could not slip past.
function pressEveryWay(button: HTMLElement): void {
	fireEvent.click(button);
	for (const key of ["Enter", " "]) {
		fireEvent.keyDown(button, { key });
		fireEvent.keyUp(button, { key });
		fireEvent.click(button);
	}
}

describe("FocusableDisabledButton", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("ignores click, Enter and Space while blocked, without native disabled", () => {
		const onClick = vi.fn();
		renderWithMantine(
			<FocusableDisabledButton blocked={true} onClick={onClick}>
				Eject
			</FocusableDisabledButton>,
		);

		const button = screen.getByRole("button", { name: "Eject" });
		button.focus();
		pressEveryWay(button);

		expect(onClick).not.toHaveBeenCalled();
		expect(button).toHaveProperty("disabled", false);
		expect(button.getAttribute("aria-disabled")).toBe("true");
		expect(button.hasAttribute("data-disabled")).toBe(true);
		expect(button.hasAttribute("aria-busy")).toBe(false);
		expect(document.activeElement).toBe(button);
	});

	it("submits no form and reaches no clickable parent while blocked", () => {
		const onSubmit = vi.fn((event: { preventDefault: () => void }) => event.preventDefault());
		const onParentClick = vi.fn();
		renderWithMantine(
			<form onSubmit={onSubmit}>
				<FocusableDisabledButton type="submit" blocked={true}>
					Eject
				</FocusableDisabledButton>
			</form>,
		);
		// A clickable ancestor outside React's root, which is where React dispatches; one on the root itself would fire anyway.
		document.body.addEventListener("click", onParentClick);
		try {
			pressEveryWay(screen.getByRole("button", { name: "Eject" }));
		} finally {
			document.body.removeEventListener("click", onParentClick);
		}

		expect(onSubmit).not.toHaveBeenCalled();
		expect(onParentClick).not.toHaveBeenCalled();
	});

	it("keeps focus on the same element when it turns blocked under focus", () => {
		const onClick = vi.fn();
		const { rerender } = renderWithMantine(<FocusableDisabledButton onClick={onClick}>Eject</FocusableDisabledButton>);
		const button = screen.getByRole("button", { name: "Eject" });
		button.focus();

		rerender(
			<MantineProvider env="test" theme={testMantineTheme}>
				<FocusableDisabledButton blocked={true} onClick={onClick}>
					Eject
				</FocusableDisabledButton>
			</MantineProvider>,
		);

		expect(screen.getByRole("button", { name: "Eject" })).toBe(button);
		expect(document.activeElement).toBe(button);
		expect(button.getAttribute("aria-disabled")).toBe("true");
	});

	it("shows a loader and stays focusable and inert while pending", () => {
		const onClick = vi.fn();
		renderWithMantine(
			<FocusableDisabledButton pending={true} leftSection={<span data-testid="icon" />} onClick={onClick}>
				Eject
			</FocusableDisabledButton>,
		);

		const button = screen.getByRole("button", { name: "Eject" });
		button.focus();
		pressEveryWay(button);

		expect(onClick).not.toHaveBeenCalled();
		expect(button.getAttribute("aria-busy")).toBe("true");
		expect(button.getAttribute("aria-disabled")).toBe("true");
		expect(button.hasAttribute("data-disabled")).toBe(true);
		// Mantine's own `loading` would set data-loading, which drops the disabled style; the loader sits in the section.
		expect(button.hasAttribute("data-loading")).toBe(false);
		expect(button.querySelector(".mantine-Loader-root")).not.toBeNull();
		expect(screen.queryByTestId("icon")).toBeNull();
		expect(button).toHaveProperty("disabled", false);
		expect(document.activeElement).toBe(button);
	});

	it("acts on a click and carries no disabled markers while available", () => {
		const onClick = vi.fn();
		renderWithMantine(
			<FocusableDisabledButton leftSection={<span data-testid="icon" />} onClick={onClick}>
				Eject
			</FocusableDisabledButton>,
		);

		const button = screen.getByRole("button", { name: "Eject" });
		fireEvent.click(button);

		expect(onClick).toHaveBeenCalledTimes(1);
		expect(button.hasAttribute("aria-disabled")).toBe(false);
		expect(button.hasAttribute("data-disabled")).toBe(false);
		expect(button.hasAttribute("aria-busy")).toBe(false);
		expect(screen.getByTestId("icon")).toBeTruthy();
	});
});
