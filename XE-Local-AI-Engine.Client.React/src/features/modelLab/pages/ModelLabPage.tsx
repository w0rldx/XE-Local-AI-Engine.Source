import { Loader, Tabs } from "@mantine/core";
import { IconFlask } from "@tabler/icons-react";
import { Suspense, use, useState } from "react";
import { useTranslation } from "react-i18next";

import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { InstalledModelInspector } from "@/features/modelLab/components/InstalledModelInspector";
import { SamePromptComparison } from "@/features/modelLab/components/SamePromptComparison";
import { TraceExperiments } from "@/features/modelLab/components/TraceExperiments";
import { forgetFailedModelLabStrings, loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";

export function ModelLabPage() {
	// Once per mount, outside the boundary that suspends.
	useState(forgetFailedModelLabStrings);
	return (
		<Suspense fallback={<Loader size="sm" />}>
			<ModelLabContent />
		</Suspense>
	);
}

function ModelLabContent() {
	const { t, i18n } = useTranslation();
	use(loadModelLabStrings(i18n.language));
	return (
		<PageShell>
			<PageHeader
				icon={<IconFlask size={24} />}
				title={t("pages.modelLab.title", "Model Lab")}
				subtitle={t(
					"pages.modelLab.subtitle",
					"Compare two local models on one prompt, inspect a model's file, or trace and steer a checkpoint. Only traces and steering runs are saved.",
				)}
			/>
			{/* Both panels stay mounted, so switching tabs neither cancels a run nor drops a chosen model. */}
			<Tabs defaultValue="compare">
				<Tabs.List>
					<Tabs.Tab value="compare">{t("pages.modelLab.compare.tab", "Same-prompt A/B")}</Tabs.Tab>
					<Tabs.Tab value="inspect">{t("pages.models.inspect.tab", "Inspect")}</Tabs.Tab>
					<Tabs.Tab value="trace">{t("pages.modelLab.trace.tab", "Trace and steer")}</Tabs.Tab>
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
