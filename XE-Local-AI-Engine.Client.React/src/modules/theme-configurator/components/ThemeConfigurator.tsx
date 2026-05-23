import { Button, Input, MantineProvider, Paper, Text } from "@mantine/core";
import { useMemo, useReducer, useRef, type ChangeEventHandler } from "react";
import { useTranslation } from "react-i18next";

import { normalizeThemeConfiguration, type ThemeConfiguration } from "@/core/theme/config/ThemeConfiguration";
import { useThemeStore } from "@/core/theme/stores/ThemeStore";
import { toast } from "@/core/ui/notifications/Toast";
import {
	defaultPrimaryBaseColor,
	defaultSecondaryBaseColor,
	downloadThemeConfiguration,
	parseColor,
	previewScopeSelector,
	readScale,
	variantPreviewItems,
} from "@/modules/theme-configurator/components/ColorUtils";
import { PaletteGeneratorSection } from "@/modules/theme-configurator/components/PaletteGeneratorSection";
import { createDraftPreviewTheme, withGeneratedPaletteScale } from "@/modules/theme-configurator/components/ThemePreviewUtils";

interface ThemeDraftState {
	source: ThemeConfiguration;
	draft: ThemeConfiguration;
}

type ThemeDraftAction =
	| { type: "replace"; source: ThemeConfiguration; draft: ThemeConfiguration }
	| { type: "update"; source: ThemeConfiguration; updater: (currentDraft: ThemeConfiguration) => ThemeConfiguration };

function createThemeDraftState(themeConfiguration: ThemeConfiguration): ThemeDraftState {
	return {
		source: themeConfiguration,
		draft: themeConfiguration,
	};
}

function synchronizeThemeDraftState(state: ThemeDraftState, source: ThemeConfiguration): ThemeDraftState {
	return state.source === source ? state : createThemeDraftState(source);
}

function themeDraftReducer(state: ThemeDraftState, action: ThemeDraftAction): ThemeDraftState {
	if (action.type === "replace") {
		return {
			source: action.source,
			draft: action.draft,
		};
	}

	const synchronizedState = synchronizeThemeDraftState(state, action.source);
	return {
		source: action.source,
		draft: action.updater(synchronizedState.draft),
	};
}

export function ThemeConfigurator() {
	const { t } = useTranslation();
	const themeConfiguration = useThemeStore((state) => state.themeConfiguration);
	const applyThemeConfiguration = useThemeStore((state) => state.applyThemeConfiguration);
	const resetThemeConfiguration = useThemeStore((state) => state.resetThemeConfiguration);
	const [draftState, dispatchDraftState] = useReducer(themeDraftReducer, themeConfiguration, createThemeDraftState);
	const fileInputReference = useRef<HTMLInputElement | null>(null);
	const synchronizedDraftState = synchronizeThemeDraftState(draftState, themeConfiguration);
	const draft = synchronizedDraftState.draft;

	const previewTheme = useMemo(() => createDraftPreviewTheme(draft), [draft]);
	const primaryBaseColor = parseColor(draft.palette.primary.main, defaultPrimaryBaseColor);
	const secondaryBaseColor = parseColor(draft.palette.secondary.main, defaultSecondaryBaseColor);
	const primaryScale = readScale(draft.palette.primary.scale, primaryBaseColor);
	const secondaryScale = readScale(draft.palette.secondary.scale, secondaryBaseColor);

	const updateDraft = (updater: (currentDraft: ThemeConfiguration) => ThemeConfiguration) => {
		dispatchDraftState({ type: "update", source: themeConfiguration, updater });
	};

	const handleApplyPreview = () => {
		const normalized = normalizeThemeConfiguration(draft);
		applyThemeConfiguration(normalized);
		toast.success(t("pages.userSettings.themeConfigurator.previewApplied"));
	};

	const handleResetPreview = () => {
		resetThemeConfiguration();
		toast.info(t("pages.userSettings.themeConfigurator.previewReset"));
	};

	const handleExport = () => {
		downloadThemeConfiguration(themeConfiguration);
		toast.success(t("pages.userSettings.themeConfigurator.exported"));
	};

	const handleImportClick = () => {
		fileInputReference.current?.click();
	};

	const handleImportFile: ChangeEventHandler<HTMLInputElement> = async (event) => {
		const file = event.target.files?.[0];
		event.target.value = "";

		if (!file) {
			return;
		}

		try {
			const content = await file.text();
			const parsed = JSON.parse(content);
			const normalized = normalizeThemeConfiguration(parsed);
			dispatchDraftState({ type: "replace", source: themeConfiguration, draft: normalized });
			applyThemeConfiguration(normalized);
			toast.success(t("pages.userSettings.themeConfigurator.imported"));
		} catch {
			toast.error(t("pages.userSettings.themeConfigurator.importError"));
		}
	};

	return (
		<div className="flex flex-col gap-5">
			<Text size="sm">{t("pages.userSettings.themeConfigurator.description")}</Text>
			<Text size="sm">{t("pages.userSettings.themeConfigurator.developerHint")}</Text>
			<Text size="sm">{t("pages.userSettings.themeConfigurator.generatorHint")}</Text>

			<div className="grid grid-cols-1 md:grid-cols-2 gap-4">
				<label className="flex flex-col gap-1">
					<Text size="sm">{t("pages.userSettings.themeConfigurator.mode")}</Text>
					<select
						className="rounded border border-zinc-300 p-2"
						value={draft.palette.mode}
						onChange={(event) => {
							const mode = event.target.value === "dark" ? "dark" : "light";
							updateDraft((currentDraft) => ({
								...currentDraft,
								palette: {
									...currentDraft.palette,
									mode,
								},
							}));
						}}
					>
						<option value="light">{t("theme.light")}</option>
						<option value="dark">{t("theme.dark")}</option>
					</select>
				</label>

				<label className="flex flex-col gap-1">
					<Text size="sm">{t("pages.userSettings.themeConfigurator.fontFamily")}</Text>
					<input
						className="rounded border border-zinc-300 p-2"
						value={draft.typography.fontFamily}
						onChange={(event) => {
							const value = event.target.value;
							updateDraft((currentDraft) => ({
								...currentDraft,
								typography: {
									...currentDraft.typography,
									fontFamily: value,
								},
							}));
						}}
					/>
				</label>
			</div>

			<div className="grid grid-cols-1 2xl:grid-cols-2 gap-6">
				<PaletteGeneratorSection
					title={t("pages.userSettings.themeConfigurator.primaryPalette")}
					description={t("pages.userSettings.themeConfigurator.primaryPaletteDescription")}
					baseColorLabel={t("pages.userSettings.themeConfigurator.baseColor")}
					generatedScaleLabel={t("pages.userSettings.themeConfigurator.generatedScale")}
					invalidColorLabel={t("pages.userSettings.themeConfigurator.invalidColor")}
					baseColor={primaryBaseColor}
					scale={primaryScale}
					onBaseColorChange={(nextBaseColor) => {
						updateDraft((currentDraft) => withGeneratedPaletteScale(currentDraft, "primary", nextBaseColor));
					}}
				/>

				<PaletteGeneratorSection
					title={t("pages.userSettings.themeConfigurator.secondaryPalette")}
					description={t("pages.userSettings.themeConfigurator.secondaryPaletteDescription")}
					baseColorLabel={t("pages.userSettings.themeConfigurator.baseColor")}
					generatedScaleLabel={t("pages.userSettings.themeConfigurator.generatedScale")}
					invalidColorLabel={t("pages.userSettings.themeConfigurator.invalidColor")}
					baseColor={secondaryBaseColor}
					scale={secondaryScale}
					onBaseColorChange={(nextBaseColor) => {
						updateDraft((currentDraft) => withGeneratedPaletteScale(currentDraft, "secondary", nextBaseColor));
					}}
				/>
			</div>

			<Paper withBorder={true} radius="md" p="md">
				<Input.Label size="sm">{t("pages.userSettings.themeConfigurator.breakpoints")}</Input.Label>
				<div className="grid grid-cols-2 md:grid-cols-5 gap-3 mt-2">
					{(["xs", "sm", "md", "lg", "xl"] as const).map((key) => (
						<label key={key} className="flex flex-col gap-1">
							<Text size="sm">{key.toUpperCase()}</Text>
							<input
								type="number"
								className="rounded border border-zinc-300 p-2"
								value={draft.breakpoints.values[key]}
								onChange={(event) => {
									const numericValue = Number(event.target.value);
									updateDraft((currentDraft) => ({
										...currentDraft,
										breakpoints: {
											...currentDraft.breakpoints,
											values: {
												...currentDraft.breakpoints.values,
												[key]: Number.isFinite(numericValue) ? numericValue : 0,
											},
										},
									}));
								}}
							/>
						</label>
					))}
				</div>
			</Paper>

			<Paper withBorder={true} radius="md" p="md">
				<Text size="sm" fw={600} mb="sm">
					{t("pages.userSettings.themeConfigurator.variantsPreview")}
				</Text>

				<div className="theme-configurator-preview-scope">
					<MantineProvider theme={previewTheme} forceColorScheme={draft.palette.mode} cssVariablesSelector={previewScopeSelector}>
						<div className="flex flex-col gap-3">
							{(["primary", "secondary"] as const).map((colorName) => (
								<div key={colorName} className="rounded border border-zinc-200 p-3">
									<Text size="sm" fw={600}>
										{colorName === "primary"
											? t("pages.userSettings.themeConfigurator.variantsPrimary")
											: t("pages.userSettings.themeConfigurator.variantsSecondary")}
									</Text>
									<div className="grid grid-cols-2 md:grid-cols-4 gap-2 mt-2">
										{variantPreviewItems.map((item) => (
											<Button key={`${colorName}-${item.variant}`} color={colorName} variant={item.variant} fullWidth={true}>
												{t(item.labelKey)}
											</Button>
										))}
									</div>
								</div>
							))}
						</div>
					</MantineProvider>
				</div>
			</Paper>

			<div className="flex flex-wrap gap-3">
				<Button variant="filled" color="primary" onClick={handleApplyPreview}>
					{t("pages.userSettings.themeConfigurator.applyPreview")}
				</Button>
				<Button variant="outline" color="secondary" onClick={handleResetPreview}>
					{t("pages.userSettings.themeConfigurator.resetToSource")}
				</Button>
				<Button variant="outline" color="secondary" onClick={handleExport}>
					{t("pages.userSettings.themeConfigurator.exportJson")}
				</Button>
				<Button variant="outline" color="secondary" onClick={handleImportClick}>
					{t("pages.userSettings.themeConfigurator.importJson")}
				</Button>
				<input ref={fileInputReference} type="file" accept="application/json" className="hidden" onChange={handleImportFile} />
			</div>
		</div>
	);
}
