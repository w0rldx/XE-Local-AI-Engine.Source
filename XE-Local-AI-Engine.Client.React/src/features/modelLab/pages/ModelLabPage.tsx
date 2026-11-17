import { Loader, Tabs } from "@mantine/core";
import { IconFlask } from "@tabler/icons-react";
import { Suspense, use, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { StickyBar } from "@/core/ui/components/StickyBar/StickyBar";
import { scrollStuckTabsToTop } from "@/core/ui/components/StickyBar/StickyTabsScroll";
import { ArchitectureExplorer } from "@/features/modelLab/components/ArchitectureExplorer";
import { InstalledModelInspector } from "@/features/modelLab/components/InstalledModelInspector";
import { NextTokenExplorer } from "@/features/modelLab/components/NextTokenExplorer";
import { SamePromptComparison } from "@/features/modelLab/components/SamePromptComparison";
import { TraceExperiments } from "@/features/modelLab/components/TraceExperiments";
import {
	type ModelLabIntent,
	type ModelLabJump,
	type ModelLabTab,
	modelLabTabs,
} from "@/features/modelLab/models/ModelLabNavigation";
import { forgetFailedModelLabStrings, loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";

// `tab` and `onTabChange` come from the route's `?tab=`; without them (unit tests) the page keeps its own tab.
interface ModelLabPageProps {
	tab?: ModelLabTab;
	onTabChange?: (tab: ModelLabTab) => void;
}

export function ModelLabPage(props: ModelLabPageProps) {
	// Once per mount, outside the boundary that suspends.
	useState(forgetFailedModelLabStrings);
	return (
		<Suspense fallback={<Loader size="sm" />}>
			<ModelLabContent {...props} />
		</Suspense>
	);
}

function ModelLabContent({ tab, onTabChange }: ModelLabPageProps) {
	const { t, i18n } = useTranslation();
	use(loadModelLabStrings(i18n.language));
	const tabsRef = useRef<HTMLDivElement>(null);
	const [ownTab, setOwnTab] = useState<ModelLabTab>("compare");
	const [intent, setIntent] = useState<ModelLabIntent>();
	const showTab = (value: string | null): void => {
		const next = modelLabTabs.find((known) => known === value);
		if (next === undefined) {
			return;
		}
		setOwnTab(next);
		onTabChange?.(next);
		scrollStuckTabsToTop(tabsRef.current);
	};
	const jump = (to: ModelLabJump): void => {
		setIntent((previous) => ({ ...to, nonce: (previous?.nonce ?? 0) + 1 }));
		showTab(to.tab);
	};
	return (
		<PageShell>
			<PageHeader
				icon={<IconFlask size={24} />}
				title={t("pages.modelLab.title", "Model Lab")}
				subtitle={t(
					"pages.modelLab.subtitle",
					"Compare two local models on one prompt, see the candidates behind each next token, inspect a model's file or its architecture, or trace and steer a checkpoint. Only traces and steering runs are saved.",
				)}
			/>
			{/* Both panels stay mounted, so switching tabs neither cancels a run nor drops a chosen model. */}
			<Tabs ref={tabsRef} value={tab ?? ownTab} onChange={showTab}>
				<StickyBar edge="top" data-testid="model-lab-tabs-bar">
					<Tabs.List>
						<Tabs.Tab value="compare">{t("pages.modelLab.compare.tab", "Same-prompt A/B")}</Tabs.Tab>
						<Tabs.Tab value="nextToken">{t("pages.modelLab.nextToken.ui.tab", "Next token")}</Tabs.Tab>
						<Tabs.Tab value="inspect">{t("pages.models.inspect.tab", "Inspect")}</Tabs.Tab>
						<Tabs.Tab value="trace">{t("pages.modelLab.trace.tab", "Trace and steer")}</Tabs.Tab>
						<Tabs.Tab value="architecture">{t("pages.modelLab.architecture.tab", "Architecture")}</Tabs.Tab>
					</Tabs.List>
				</StickyBar>
				<Tabs.Panel value="compare" pt="md">
					<SamePromptComparison />
				</Tabs.Panel>
				<Tabs.Panel value="nextToken" pt="md">
					<NextTokenExplorer intent={intent?.tab === "nextToken" ? intent : undefined} />
				</Tabs.Panel>
				<Tabs.Panel value="inspect" pt="md">
					<InstalledModelInspector />
				</Tabs.Panel>
				<Tabs.Panel value="trace" pt="md">
					<TraceExperiments intent={intent?.tab === "trace" ? intent : undefined} onJump={jump} />
				</Tabs.Panel>
				<Tabs.Panel value="architecture" pt="md">
					<ArchitectureExplorer intent={intent?.tab === "architecture" ? intent : undefined} onJump={jump} />
				</Tabs.Panel>
			</Tabs>
		</PageShell>
	);
}
