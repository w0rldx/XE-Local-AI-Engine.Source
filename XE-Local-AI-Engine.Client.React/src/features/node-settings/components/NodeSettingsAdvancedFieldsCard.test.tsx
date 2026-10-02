// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	type NodeSettingsAdvancedFieldsCardProps,
	NodeSettingsAgentLimitsCard,
	NodeSettingsAgentWorkspacesCard,
} from "@/features/node-settings/components/NodeSettingsAdvancedFieldsCard";
import { toNodeSettingsFieldBounds, toNodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";
import { testMantineTheme } from "@/test/MantineTestRender";

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

function renderCards(): void {
	const props: NodeSettingsAdvancedFieldsCardProps = {
		form: toNodeSettingsFieldsForm(undefined),
		bounds: toNodeSettingsFieldBounds(undefined),
		errors: {},
		onChange: vi.fn() as unknown as NodeSettingsAdvancedFieldsCardProps["onChange"],
	};
	render(
		<MantineProvider env="test" theme={testMantineTheme}>
			<NodeSettingsAgentLimitsCard {...props} />
			<NodeSettingsAgentWorkspacesCard {...props} />
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
		renderCards();

		expect(screen.getByLabelText("Disconnect grace")).toBeTruthy();
		expect(
			screen.getByText(
				"Allowed range: 0–86400 seconds. How long a run keeps going after its last client disconnects. 0 never cancels.",
			),
		).toBeTruthy();
		expectInputSuffix("node-settings-detached-grace-seconds", "seconds");
	});

	it("shows both AgentHome timeouts as live-reload settings in minutes", () => {
		renderCards();

		expect(screen.getByLabelText("AgentHome prepare timeout")).toBeTruthy();
		expect(screen.getByLabelText("AgentHome command timeout")).toBeTruthy();
		// 1–86400 s on the wire, shown in minutes; the 1 s floor is no clean minute value, so it stays in seconds.
		expect(screen.getAllByText("Allowed range: 1 s–1440 minutes. Changes take effect without restarting the node.")).toHaveLength(
			2,
		);
		expect((screen.getByTestId("node-settings-agenthome-prepare-timeout") as HTMLInputElement).value).toBe("15 minutes");
		expectInputSuffix("node-settings-agenthome-command-timeout", "minutes");
	});

	it("shows the AgentHome byte caps in MB with live-reload guidance", () => {
		renderCards();

		expect(screen.getByLabelText("AgentHome max selected folder size")).toBeTruthy();
		expect(screen.getByLabelText("AgentHome max patch size")).toBeTruthy();
		expect(screen.getAllByText("A positive size in MB. Changes take effect without restarting the node.")).toHaveLength(2);
		// 536870912 / 52428800 bytes on the wire.
		expect((screen.getByTestId("node-settings-agenthome-max-folder-bytes") as HTMLInputElement).value).toBe("512 MB");
		expect((screen.getByTestId("node-settings-agenthome-max-patch-bytes") as HTMLInputElement).value).toBe("50 MB");
	});

	it("adds the run time limit in minutes and the run retention in days", () => {
		renderCards();

		expect((screen.getByTestId("node-settings-agenthome-max-run") as HTMLInputElement).value).toBe("10 minutes");
		expect((screen.getByTestId("node-settings-agenthome-run-retention") as HTMLInputElement).value).toBe("30 days");
		expect(
			screen.getByText(
				"Allowed range: 1–1440 minutes. The longest a whole run may take; at least the command timeout. Applies to the next run.",
			),
		).toBeTruthy();
	});

	it("adds the inner tool-call budget, the patch-apply timeout and the run caps, the byte cap in GB", () => {
		renderCards();

		expect((screen.getByTestId("node-settings-agenthome-max-inner-tool-calls") as HTMLInputElement).value).toBe("24");
		expect((screen.getByTestId("node-settings-agenthome-patch-apply-timeout") as HTMLInputElement).value).toBe("120 seconds");
		expect((screen.getByTestId("node-settings-agenthome-run-retention-max-runs") as HTMLInputElement).value).toBe("200");
		expect((screen.getByTestId("node-settings-agenthome-run-retention-max-bytes") as HTMLInputElement).value).toBe("2 GB");
		// All four, and the retention window, are read per run, apply or sweep, so none carries the restart badge.
		expect(screen.queryByTestId("node-settings-restart-badge-agentHomeRunRetentionDays")).toBeNull();
		expect(screen.queryByTestId("node-settings-restart-badge-agentHomeMaxInnerToolCalls")).toBeNull();
	});
});
