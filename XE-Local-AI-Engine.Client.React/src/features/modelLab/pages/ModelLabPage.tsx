import { Tabs } from "@mantine/core";
import { IconFlask } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { InstalledModelInspector } from "@/features/modelLab/components/InstalledModelInspector";
import { SamePromptComparison } from "@/features/modelLab/components/SamePromptComparison";
import { TraceExperiments } from "@/features/modelLab/components/TraceExperiments";

export function ModelLabPage() {
	const { t } = useTranslation();
	return (
		<PageShell>
			<PageHeader
				icon={<IconFlask size={24} />}
				title={t("pages.modelLab.title", "Model Lab")}
				subtitle={t(
					"pages.modelLab.subtitle",
					"Compare two local models on one prompt, inspect an installed model's file, or trace a checkpoint's layers. Only traces are saved.",
				)}
			/>
			{/* Both panels stay mounted, so switching tabs neither cancels a run nor drops a chosen model. */}
			<Tabs defaultValue="compare">
				<Tabs.List>
					<Tabs.Tab value="compare">{t("pages.modelLab.compare.tab", "Same-prompt A/B")}</Tabs.Tab>
					<Tabs.Tab value="inspect">{t("pages.models.inspect.tab", "Inspect")}</Tabs.Tab>
					<Tabs.Tab value="trace">{t("pages.modelLab.trace.tab", "Trace")}</Tabs.Tab>
				</Tabs.List>
				<Tabs.Panel value="compare" pt="md">
					<SamePromptComparison />
				</Tabs.Panel>
				<Tabs.Panel value="inspect" pt="md">
					<InstalledModelInspector />
				</Tabs.Panel>
				<Tabs.Panel value="trace" pt="md">
					<TraceExperiments />
				</Tabs.Panel>
			</Tabs>
		</PageShell>
	);
}
