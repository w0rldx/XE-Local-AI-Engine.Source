// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import i18next from "i18next";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import "@/i18n";
import { IntegrationExecutionTable } from "@/features/integrations/components/IntegrationExecutionTable";
import de from "@/locales/de.json";
import { installJsdomEnvironmentMocks, testMantineTheme } from "@/test/MantineTestRender";

describe("IntegrationExecutionTable", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(async () => {
		cleanup();
		await i18next.changeLanguage("en");
	});

	it("shows the German label of a failure category, not the wire code", async () => {
		i18next.addResourceBundle("de", "translation", de, true, true);
		await i18next.changeLanguage("de");

		render(
			<MantineProvider env="test" theme={testMantineTheme}>
				<IntegrationExecutionTable
					executions={[
						{
							id: "exec-cloud",
							triggerId: "trigger-1",
							sessionId: "5c0ffee0-0000-0000-0000-000000000001",
							status: "Failed",
							receivedAtUtc: 1_700_000_000_000,
							startedAtUtc: null,
							endedAtUtc: 1_700_000_000_010,
							failureCategory: "cloud-model-rejected",
							failureSummary: null,
							outputCount: 0,
						},
					]}
					triggers={[]}
					isCancelling={false}
					onView={() => undefined}
					onCancel={() => undefined}
				/>
			</MantineProvider>,
		);

		expect(screen.getByTestId("integration-execution-category-exec-cloud").textContent).toBe("Cloud-Modell abgelehnt");
	});
});
