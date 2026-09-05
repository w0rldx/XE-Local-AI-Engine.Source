// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	NodeSettingsAdvancedFieldsCard,
	type NodeSettingsAdvancedFieldsCardProps,
} from "@/features/node-settings/components/NodeSettingsAdvancedFieldsCard";
import { toNodeSettingsFieldBounds, toNodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, fallback?: string) => fallback ?? _key,
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
}

function renderCard(): void {
	render(
		<MantineProvider>
			<NodeSettingsAdvancedFieldsCard
				form={toNodeSettingsFieldsForm(undefined)}
				bounds={toNodeSettingsFieldBounds(undefined)}
				errors={{}}
				onChange={vi.fn() as unknown as NodeSettingsAdvancedFieldsCardProps["onChange"]}
			/>
		</MantineProvider>,
	);
}

function expectInputSuffix(testId: string, suffix: string): void {
	const input = screen.getByTestId(testId) as HTMLInputElement;
	// biome-ignore lint/suspicious/noMisplacedAssertion: the whole point of `expectInputSuffix` is to assert for its callers, which are tests.
	expect(input.value).toMatch(new RegExp(`${suffix}$`));
}

describe("NodeSettingsAdvancedFieldsCard guidance", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => cleanup());

	it("explains disconnect grace semantics and displays its seconds unit", () => {
		renderCard();

		expect(screen.getByLabelText("Disconnect grace")).toBeTruthy();
		expect(
			screen.getByText(
				"Allowed range: 0–86400 seconds. How long a run keeps going after its last client disconnects. 0 never cancels.",
			),
		).toBeTruthy();
		expectInputSuffix("node-settings-detached-grace-seconds", "seconds");
	});

	it("marks both AgentHome timeouts as live-reload settings measured in seconds", () => {
		renderCard();

		expect(screen.getByLabelText("AgentHome prepare timeout")).toBeTruthy();
		expect(screen.getByLabelText("AgentHome command timeout")).toBeTruthy();
		expect(screen.getAllByText("Allowed range: 1–86400 seconds. Changes take effect without restarting the node.")).toHaveLength(
			2,
		);
		expectInputSuffix("node-settings-agenthome-prepare-timeout", "seconds");
		expectInputSuffix("node-settings-agenthome-command-timeout", "seconds");
	});

	it("retains byte-limit labels and live-reload guidance", () => {
		renderCard();

		expect(screen.getByLabelText("AgentHome max selected folder size")).toBeTruthy();
		expect(screen.getByLabelText("AgentHome max patch size")).toBeTruthy();
		expect(screen.getAllByText("A positive number of bytes. Changes take effect without restarting the node.")).toHaveLength(2);
		expectInputSuffix("node-settings-agenthome-max-folder-bytes", "bytes");
		expectInputSuffix("node-settings-agenthome-max-patch-bytes", "bytes");
	});
});
