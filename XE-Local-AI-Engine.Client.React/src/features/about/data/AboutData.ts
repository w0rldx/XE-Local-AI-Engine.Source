import { environment } from "@/Environment";
import generatedLicenses from "@/features/about/data/third-party-licenses.generated.json";

export interface IApplicationInfo {
	title: string;
	version: string;
	description: string;
	repositoryUrl?: string;
	websiteUrl?: string;
	copyright: string;
}

export interface IThirdPartyPackage {
	id: string;
	name: string;
	version: string;
	license: string;
	homepageUrl?: string;
	source?: "frontend" | "backend";
}

interface IGeneratedLicenseFile {
	readonly packages: readonly IThirdPartyPackage[];
}

export const applicationInfo: IApplicationInfo = {
	title: environment.VITE_APP_TITLE,
	// Injected at build time from Directory.Build.props (the single version source of truth, also used by
	// `vpk --packVersion`). See vite.config.ts `resolveAppVersion()`.
	version: environment.VITE_APP_VERSION,
	description:
		"Local AI engine for running, managing, and chatting with on-device AI models.",
	copyright: `© ${new Date().getFullYear()} XE AI-Engine`,
};

/**
 * Bundled third-party packages shown in the About dialog's Licenses tab. The list
 * is auto-generated for BOTH the frontend (npm) and backend (.NET NuGet) by
 * `pnpm run licenses:generate` (scripts/GenerateLicenses.mjs) and committed as
 * third-party-licenses.generated.json. Do not hand-edit the generated file.
 */
export const thirdPartyPackages: readonly IThirdPartyPackage[] = (generatedLicenses as IGeneratedLicenseFile).packages;
