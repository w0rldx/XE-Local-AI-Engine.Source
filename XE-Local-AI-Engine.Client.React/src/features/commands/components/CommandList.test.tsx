// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import i18next from "i18next";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import "@/i18n";
import { CommandList } from "@/features/commands/components/CommandList";
import type { SlashCommand } from "@/features/commands/models/CommandModels";
import de from "@/locales/de.json";
import { installJsdomEnvironmentMocks, testMantineTheme } from "@/test/MantineTestRender";

const commands: SlashCommand[] = [
	{
		id: null,
		name: "ping",
		description: "Test the current chat agent.",
		source: "builtIn",
		action: { type: "SendPrompt", prompt: "Respond with exactly PONG" },
	},
	{
		id: "c1",
		name: "summary",
		description: "Fasse den Verlauf zusammen.",
		source: "custom",
		action: { type: "SendPrompt", prompt: "Summarize" },
	},
];

describe("CommandList", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(async () => {
		cleanup();
		await i18next.changeLanguage("en");
	});

	it("localizes a built-in command's description and keeps a custom one as written", async () => {
		i18next.addResourceBundle("de", "translation", de, true, true);
		await i18next.changeLanguage("de");

		render(
			<MantineProvider env="test" theme={testMantineTheme}>
				<CommandList commands={commands} isMutating={false} onEdit={() => undefined} onDelete={() => undefined} />
			</MantineProvider>,
		);

		expect(screen.getByText("Den aktuellen Chat-Agenten testen.")).toBeTruthy();
		expect(screen.queryByText("Test the current chat agent.")).toBeNull();
		expect(screen.getByText("Fasse den Verlauf zusammen.")).toBeTruthy();
	});

	it("drops the actions column when every command is a built-in", () => {
		const builtIns = commands.filter((command) => command.source === "builtIn");
		render(
			<MantineProvider env="test" theme={testMantineTheme}>
				<CommandList commands={builtIns} isMutating={false} onEdit={() => undefined} onDelete={() => undefined} />
			</MantineProvider>,
		);

		expect(screen.queryByText("Actions")).toBeNull();
		expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
	});

	it("marks a built-in row with a dash beside editable custom rows", () => {
		render(
			<MantineProvider env="test" theme={testMantineTheme}>
				<CommandList commands={commands} isMutating={false} onEdit={() => undefined} onDelete={() => undefined} />
			</MantineProvider>,
		);

		expect(screen.getByTestId("command-row-ping").textContent).toContain("—");
		expect(screen.getAllByRole("button", { name: "Edit" })).toHaveLength(1);
	});
});
