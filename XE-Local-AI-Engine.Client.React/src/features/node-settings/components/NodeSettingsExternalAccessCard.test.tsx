// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { NodeSettingsExternalAccessCard } from "@/features/node-settings/components/NodeSettingsExternalAccessCard";
import { type NodeSettingsFieldsForm, toNodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

// Deterministic i18n: t returns the supplied default so the human copy is asserted, not the raw key. This file asserts
// CONTROL STATE and CALLBACKS, never prose — the R18/R18a copy itself is asserted against the real bundles in
// ExternalAccessLocalization.test.ts and, rendered, in ExternalAccessSetup.test.tsx.
vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, fallback?: string) => fallback ?? key,
	}),
}));

function installJsdomEnvironmentMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
	// jsdom does not implement scrollIntoView; Mantine's Combobox calls it on a timer when a dropdown opens, which
	// surfaces as an unhandled error AFTER the test that opened it.
	Element.prototype.scrollIntoView = vi.fn();
}

function renderCard(formOverrides: Partial<NodeSettingsFieldsForm> = {}): {
	onChange: ReturnType<typeof vi.fn>;
	onApplyPreset: ReturnType<typeof vi.fn>;
} {
	const onChange = vi.fn();
	const onApplyPreset = vi.fn();
	render(
		<MantineProvider>
			<NodeSettingsExternalAccessCard
				form={{ ...toNodeSettingsFieldsForm(undefined), ...formOverrides }}
				onChange={onChange}
				onApplyPreset={onApplyPreset}
			/>
		</MantineProvider>,
	);
	return { onChange, onApplyPreset };
}

function switchInput(testId: string): HTMLInputElement {
	const host = screen.getByTestId(testId);
	return (host.querySelector("input[type='checkbox']") ?? host) as HTMLInputElement;
}

describe("NodeSettingsExternalAccessCard", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("renders the three external-access switches and no restart hint", () => {
		renderCard({ autoCheckApplicationUpdates: true, autoCheckRuntimeUpdates: false, autoProvisionFirstRunModel: true });

		expect(switchInput("node-settings-auto-check-application-updates").checked).toBe(true);
		expect(switchInput("node-settings-auto-check-runtime-updates").checked).toBe(false);
		expect(switchInput("node-settings-auto-provision-first-run-model").checked).toBe(true);
		// None of the four is restart-gated. The set-membership guard lives in NodeSettingsFieldsModel.test.ts; this only
		// pins that the card renders no hint.
		expect(screen.queryByTestId("node-settings-restart-hint-externalAccessProfile")).toBeNull();
	});

	it("calls onChange with the switch key when a toggle is flipped", () => {
		const { onChange, onApplyPreset } = renderCard({ autoCheckRuntimeUpdates: true });

		fireEvent.click(switchInput("node-settings-auto-check-runtime-updates"));

		expect(onChange).toHaveBeenCalledWith("autoCheckRuntimeUpdates", false);
		expect(onApplyPreset).not.toHaveBeenCalled();
	});

	it("calls onApplyPreset when a profile is selected", () => {
		const { onApplyPreset, onChange } = renderCard({ externalAccessProfile: "recommended" });

		fireEvent.click(screen.getByTestId("node-settings-external-access-profile"));
		fireEvent.click(screen.getByText("Offline / Manual"));

		expect(onApplyPreset).toHaveBeenCalledWith("offline");
		// A preset is a command, not a field edit — it must never reach the generic field handler.
		expect(onChange).not.toHaveBeenCalled();
	});

	it("offers custom as a display-only option that cannot be chosen", () => {
		// The server stamps "custom" when a save carries switches and no profile, so the control has to SHOW it — but the
		// client must never send it, which is why the option is disabled.
		const { onApplyPreset } = renderCard({ externalAccessProfile: "custom" });

		const select = screen.getByTestId("node-settings-external-access-profile") as HTMLInputElement;
		expect(select.value).toBe("Custom");

		fireEvent.click(select);
		const listbox = screen.getByRole("listbox", { hidden: true });
		const customOption = within(listbox).getByRole("option", { name: "Custom", hidden: true });
		// Mantine marks a disabled Combobox option with `data-combobox-disabled`, not `data-disabled`/`aria-disabled`.
		expect(customOption.getAttribute("data-combobox-disabled")).toBe("true");

		fireEvent.click(customOption);
		expect(onApplyPreset).not.toHaveBeenCalled();
	});

	it("renders the offline honesty clause on the settings card", () => {
		renderCard();

		expect(screen.getByTestId("node-settings-external-access-does-not-block")).toBeTruthy();
	});

	it("renders an unset profile as not chosen with both presets selectable", () => {
		// A corrupted settings file reads back as null. The first-run route keys on "pending" and will not show, so this
		// page is the only surface that can resolve it — and it must not claim Recommended on the node's behalf.
		const { onApplyPreset } = renderCard({ externalAccessProfile: "" });

		const select = screen.getByTestId("node-settings-external-access-profile") as HTMLInputElement;
		expect(select.value).toBe("");
		expect(select.getAttribute("placeholder")).toBe("Not chosen");

		fireEvent.click(select);
		const listbox = screen.getByRole("listbox", { hidden: true });
		expect(within(listbox).getByRole("option", { name: "Recommended", hidden: true })).toBeTruthy();

		fireEvent.click(within(listbox).getByRole("option", { name: "Recommended", hidden: true }));
		expect(onApplyPreset).toHaveBeenCalledWith("recommended");
	});
});
