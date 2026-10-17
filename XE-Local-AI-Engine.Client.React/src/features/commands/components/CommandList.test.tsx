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
});
