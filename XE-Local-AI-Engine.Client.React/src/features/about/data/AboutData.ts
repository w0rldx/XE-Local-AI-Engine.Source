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
	// No version is exposed via package.json / env yet. Bump here, or wire to a
	// build-time define (e.g. import.meta.env.VITE_APP_VERSION) once one exists.
	version: "0.1.0",
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
