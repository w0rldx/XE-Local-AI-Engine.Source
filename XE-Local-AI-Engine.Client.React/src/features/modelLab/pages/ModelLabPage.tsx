import { Alert, Group, Loader, Select, Stack, Tabs, Text } from "@mantine/core";
import { IconFlask } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { Suspense, use, useCallback, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { StickyBar } from "@/core/ui/components/StickyBar/StickyBar";
import { scrollStuckTabsToTop } from "@/core/ui/components/StickyBar/StickyTabsScroll";
import { ArchitectureExplorer } from "@/features/modelLab/components/ArchitectureExplorer";
import { InstalledModelInspector } from "@/features/modelLab/components/InstalledModelInspector";
import { ModelPickerNotice } from "@/features/modelLab/components/ModelPickerNotice";
import { NextTokenExplorer } from "@/features/modelLab/components/NextTokenExplorer";
import { PlaygroundPanel } from "@/features/modelLab/components/PlaygroundPanel";
import { SamePromptComparison } from "@/features/modelLab/components/SamePromptComparison";
import { TraceExperiments } from "@/features/modelLab/components/TraceExperiments";
import type { ArchitectureSelection } from "@/features/modelLab/models/ModelLabArchitecture";
import type { ExplanationLevel } from "@/features/modelLab/models/ModelLabExplanation";
import {
	type ArchitectureDetailPatch,
	applyModelLabPatch,
	defaultModelLabTab,
	installedGgufModels,
	jumpPatch,
	type ModelLabIntent,
	type ModelLabJump,
	type ModelLabSearch,
	type ModelLabUpdate,
	modelLabTabs,
	sameExperimentId,
} from "@/features/modelLab/models/ModelLabNavigation";
import { forgetFailedModelLabStrings, loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";

// The route hands down the URL's search and takes every change; without either (unit tests) the page keeps its own.
// `onShown` runs only once the router shows that change: never for a blocked or failed one. `afterNavigation` runs a
// scroll once the router has restored its scroll positions for the navigation it is showing; without it, at once.
type ModelLabPageProps =
	| {
			search: ModelLabSearch;
			onSearchChange: (update: ModelLabUpdate, push: boolean, onShown?: () => void) => void;
			afterNavigation?: (run: () => void) => void;
	  }
	| { search?: undefined; onSearchChange?: undefined; afterNavigation?: undefined };

const runNow = (run: () => void): void => run();

// The tabs the page's model picker drives; A/B and Trace choose their own models.
const pickerTabs: ReadonlySet<string> = new Set(["architecture", "nextToken", "inspect"]);
// The URL fields the Architecture tab shows besides its model.
const architectureFields = ["block", "trace", "op", "group", "tensor"] as const;

export function ModelLabPage(props: ModelLabPageProps) {
	// Once per mount, outside the boundary that suspends.
	useState(forgetFailedModelLabStrings);
	return (
		<Suspense fallback={<Loader size="sm" />}>
			<ModelLabContent {...props} />
		</Suspense>
	);
}

function ModelLabContent({ search: routeSearch, onSearchChange, afterNavigation = runNow }: ModelLabPageProps) {
	const { t, i18n } = useTranslation();
	use(loadModelLabStrings(i18n.language));
	const tabsRef = useRef<HTMLDivElement>(null);
	const [ownSearch, setOwnSearch] = useState<ModelLabSearch>({});
	const search = routeSearch ?? ownSearch;
	const activeTab = search.tab ?? defaultModelLabTab;
	// Every change goes through one transition; only a jump pushes a history entry.
	const write = useCallback(
		(update: ModelLabUpdate, push = false, onShown?: () => void): void => {
			if (onSearchChange) {
				onSearchChange(update, push, onShown);
			} else {
				setOwnSearch((previous) => applyModelLabPatch(previous, update));
				onShown?.();
			}
		},
		[onSearchChange],
	);

	const models = useQuery({ ...listLocalModelsOptions(), staleTime: 30_000 });
	const modelOptions = installedGgufModels(models.data?.items).map((model) => ({ value: model.modelName, label: model.label }));
	const modelNames = modelOptions.map((option) => option.value);
	// The URL's model, once a held list confirms it. A list kept after a failed refetch still counts.
	const resolvedModel =
		models.data !== undefined && search.model !== undefined && modelNames.includes(search.model) ? search.model : null;
	// Architecture and Inspect read a model's file only once their tab shows that model, from any way of getting there.
	// Architecture's block, trace and detail selection follow its model: a hidden tab opens no detail and reads no run
	// for a change.
	const [architectureView, setArchitectureView] = useState<
		Pick<ModelLabSearch, (typeof architectureFields)[number]> & { model: string | null }
	>({ model: null });
	if (
		activeTab === "architecture" &&
		(architectureView.model !== resolvedModel || architectureFields.some((field) => architectureView[field] !== search[field]))
	) {
		const { block, trace, op, group, tensor } = search;
		setArchitectureView({ model: resolvedModel, block, trace, op, group, tensor });
	}
	// Counts the Architecture tab showing again after another tab (Back, or a tab click): its kept detail scrolls the open
	// operation back into view once per count.
	const [shownTab, setShownTab] = useState({ tab: activeTab, architectureArrivals: 0 });
	if (shownTab.tab !== activeTab) {
		const arrived = activeTab === "architecture" ? 1 : 0;
		setShownTab({ tab: activeTab, architectureArrivals: shownTab.architectureArrivals + arrived });
	}
	const [inspectModel, setInspectModel] = useState<string | null>(null);
	if (activeTab === "inspect" && inspectModel !== resolvedModel) {
		setInspectModel(resolvedModel);
	}
	// Trace opens the URL's run only once its tab shows; an open run stays while another tab is shown.
	const [traceExperiment, setTraceExperiment] = useState<string | undefined>(undefined);
	if (activeTab === "trace" && traceExperiment !== search.experiment) {
		setTraceExperiment(search.experiment);
	}
	// The training stepper reads its list once its tab has shown, and opens the URL's run only while its tab shows; an
	// open run stays while another tab is shown.
	const [playground, setPlayground] = useState<{ shown: boolean; run?: string }>({ shown: false });
	if (activeTab === "playground" && (!playground.shown || playground.run !== search.run)) {
		setPlayground({ shown: true, run: search.run });
	}
	// Only the Next token prefill is page state; the prompt never enters the URL. A tab switch or another model ends it.
	const [nextTokenIntent, setNextTokenIntent] = useState<ModelLabIntent<"nextToken">>();
	const jumpCount = useRef(0);

	const showTab = (value: string | null): void => {
		const next = modelLabTabs.find((known) => known === value);
		if (next === undefined) {
			return;
		}
		setNextTokenIntent(undefined);
		write({ tab: next });
		scrollStuckTabsToTop(tabsRef.current);
	};
	const jump = useCallback(
		(to: ModelLabJump): void => {
			jumpCount.current += 1;
			const nonce = jumpCount.current;
			// Only a jump the router shows hands anything over: a blocked one leaves no prefill, focus or scroll behind.
			write(jumpPatch(to), true, () => {
				setNextTokenIntent(to.tab === "nextToken" ? { ...to, nonce } : undefined);
				scrollStuckTabsToTop(tabsRef.current);
				// The button that jumped is in a panel now hidden: focus goes to the target's tab instead.
				tabsRef.current?.querySelector<HTMLElement>(`[data-model-lab-tab="${to.tab}"]`)?.focus();
			});
		},
		[write],
	);
	const selectModel = useCallback(
		(model: string | null): void => {
			setNextTokenIntent(undefined);
			write({ model: model ?? undefined });
		},
		[write],
	);
	const selectBlock = useCallback(
		(block: ArchitectureSelection | null, onShown?: () => void) => write({ block: block ?? undefined }, false, onShown),
		[write],
	);
	const selectDetail = useCallback(
		(patch: ArchitectureDetailPatch, onShown?: () => void) => write(patch, false, onShown),
		[write],
	);
	const selectTrace = useCallback((trace: string | null) => write({ trace: trace ?? undefined }), [write]);
	const selectLevel = useCallback((level: ExplanationLevel) => write({ level }), [write]);
	const openExperiment = useCallback((experiment: string | undefined) => write({ experiment }), [write]);
	// A delete that finishes after another run was opened must not close that one.
	const closeDeletedExperiment = useCallback(
		(removed: string) => write((previous) => (sameExperimentId(previous.experiment, removed) ? { experiment: undefined } : {})),
		[write],
	);
	const openRun = useCallback((run: string | undefined) => write({ run }), [write]);
	const closeDeletedRun = useCallback(
		(removed: string) => write((previous) => (sameExperimentId(previous.run, removed) ? { run: undefined } : {})),
		[write],
	);

	return (
		<PageShell>
			<PageHeader
				icon={<IconFlask size={24} />}
				title={t("pages.modelLab.title", "Model Lab")}
				subtitle={t(
					"pages.modelLab.subtitle",
					"Choose an installed model to see its architecture, the candidates behind each next token or its file. Compare two local models on one prompt, trace and steer a checkpoint, or train a tiny network and step through its training. Only traces, steering runs and training runs are saved.",
				)}
			/>
			<Stack gap="xs" mb="md">
				<Select
					label={t("pages.modelLab.pageModel.label", "Model for Architecture, Next token and Inspect")}
					description={t("pages.modelLab.pageModel.hint", "Same-prompt A/B and Trace and steer choose their own models.")}
					placeholder={t("pages.directInference.modelPlaceholder", "Choose a model")}
					data={modelOptions}
					value={resolvedModel}
					onChange={selectModel}
					searchable={true}
				/>
				{pickerTabs.has(activeTab) && (
					<>
						{models.isPending && (
							<Group gap="xs" role="status" aria-live="polite" data-testid="model-lab-models-loading">
								<Loader size="xs" />
								<Text size="sm" c="dimmed">
									{t("pages.modelLab.pageModel.loading", "Loading the installed models…")}
								</Text>
							</Group>
						)}
						<ModelPickerNotice status={models.data === undefined ? models.status : "success"} modelCount={modelNames.length} />
						{/* With no model installed at all, the notice above already says so. */}
						{models.data !== undefined && search.model !== undefined && resolvedModel === null && modelNames.length > 0 && (
							// A model name is one long word: it breaks anywhere on a narrow screen instead of running out of the box.
							<Alert color="yellow" style={{ overflowWrap: "anywhere" }} data-testid="model-lab-model-not-installed">
								{t(
									"pages.modelLab.pageModel.notInstalled",
									"{{model}} is not installed as a llama.cpp model on this node. Choose an installed model.",
									{ model: search.model },
								)}
							</Alert>
						)}
					</>
				)}
			</Stack>
			{/* Every panel stays mounted, so switching tabs neither cancels a run nor drops what a panel holds. */}
			<Tabs ref={tabsRef} value={activeTab} onChange={showTab}>
				<StickyBar edge="top" data-testid="model-lab-tabs-bar">
					<Tabs.List>
						<Tabs.Tab value="architecture" data-model-lab-tab="architecture">
							{t("pages.modelLab.architecture.tab", "Architecture")}
						</Tabs.Tab>
						<Tabs.Tab value="compare" data-model-lab-tab="compare">
							{t("pages.modelLab.compare.tab", "Same-prompt A/B")}
						</Tabs.Tab>
						<Tabs.Tab value="nextToken" data-model-lab-tab="nextToken">
							{t("pages.modelLab.nextToken.ui.tab", "Next token")}
						</Tabs.Tab>
						<Tabs.Tab value="inspect" data-model-lab-tab="inspect">
							{t("pages.models.inspect.tab", "Inspect")}
						</Tabs.Tab>
						<Tabs.Tab value="trace" data-model-lab-tab="trace">
							{t("pages.modelLab.trace.tab", "Trace and steer")}
						</Tabs.Tab>
						<Tabs.Tab value="playground" data-model-lab-tab="playground">
							{t("pages.modelLab.playground.tab", "Training stepper")}
						</Tabs.Tab>
					</Tabs.List>
				</StickyBar>
				<Tabs.Panel value="architecture" pt="md">
					<ArchitectureExplorer
						modelName={architectureView.model}
						selection={architectureView.block ?? null}
						traceId={architectureView.trace ?? null}
						inside={architectureView}
						arrival={shownTab.architectureArrivals}
						afterNavigation={afterNavigation}
						level={search.level ?? "overview"}
						onSelect={selectBlock}
						onDetailChange={selectDetail}
						onTraceChange={selectTrace}
						onLevelChange={selectLevel}
						onJump={jump}
					/>
				</Tabs.Panel>
				<Tabs.Panel value="compare" pt="md">
					<SamePromptComparison />
				</Tabs.Panel>
				<Tabs.Panel value="nextToken" pt="md">
					<NextTokenExplorer modelName={resolvedModel} intent={activeTab === "nextToken" ? nextTokenIntent : undefined} />
				</Tabs.Panel>
				<Tabs.Panel value="inspect" pt="md">
					<InstalledModelInspector modelName={inspectModel} />
				</Tabs.Panel>
				<Tabs.Panel value="trace" pt="md">
					<TraceExperiments
						experimentId={traceExperiment}
						onExperimentChange={openExperiment}
						onExperimentDeleted={closeDeletedExperiment}
						onJump={jump}
					/>
				</Tabs.Panel>
				<Tabs.Panel value="playground" pt="md">
					<PlaygroundPanel shown={playground.shown} runId={playground.run} onRunChange={openRun} onRunDeleted={closeDeletedRun} />
				</Tabs.Panel>
			</Tabs>
		</PageShell>
	);
}
