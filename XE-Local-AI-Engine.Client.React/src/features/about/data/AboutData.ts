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

export interface IRuntimeLegalDocument {
	name: string;
	href: string;
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
	// Must match the holder and year in the repo-root LICENSE. Deliberately not `new Date().getFullYear()`:
	// that silently rolls over on the viewer's clock, so a build cut in 2026 would claim 2027.
	copyright: "© 2026 w0rldx — Apache-2.0 licensed",
};

/**
 * Bundled third-party packages shown in the About dialog's Licenses tab. The list
 * is auto-generated for BOTH the frontend (npm) and backend (.NET NuGet) by
 * `pnpm run licenses:generate` (scripts/GenerateLicenses.mjs) and committed as
 * third-party-licenses.generated.json. Do not hand-edit the generated file.
 */
export const thirdPartyPackages: readonly IThirdPartyPackage[] = (generatedLicenses as IGeneratedLicenseFile).packages;

/** Runtime terms copied byte-for-byte into every published app's served wwwroot. */
export const runtimeLegalDocuments: readonly IRuntimeLegalDocument[] = [
	{ name: ".NET runtime license", href: "/licenses/dotnet/DOTNET-RUNTIME-LICENSE.txt" },
	{ name: ".NET runtime third-party notices", href: "/licenses/dotnet/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt" },
	{ name: "ASP.NET Core runtime license", href: "/licenses/dotnet/ASPNETCORE-RUNTIME-LICENSE.txt" },
	{ name: "ASP.NET Core runtime third-party notices", href: "/licenses/dotnet/ASPNETCORE-RUNTIME-THIRD-PARTY-NOTICES.txt" },
	{ name: "Windows single-file .NET Library License", href: "/licenses/dotnet/DOTNET-LIBRARY-LICENSE.html" },
];
