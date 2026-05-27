import { environment } from "@/Environment";

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
}

export const applicationInfo: IApplicationInfo = {
	title: environment.VITE_APP_TITLE,
	// No version is exposed via package.json / env yet. Bump here, or wire to a
	// build-time define (e.g. import.meta.env.VITE_APP_VERSION) once one exists.
	version: "0.1.0",
	description:
		"Local AI engine worker node for the C0re platform: run, manage, and chat with on-device Ollama models.",
	copyright: `© ${new Date().getFullYear()} C0re`,
};

/**
 * Scaffold of bundled third-party packages shown in the About dialog's Licenses
 * tab. Extend this list as needed; it can later be generated from package.json.
 */
export const thirdPartyPackages: readonly IThirdPartyPackage[] = [
	{ id: "react", name: "React", version: "19.2", license: "MIT", homepageUrl: "https://react.dev" },
	{ id: "mantine", name: "@mantine/core", version: "9.2", license: "MIT", homepageUrl: "https://mantine.dev" },
	{
		id: "tanstack-query",
		name: "@tanstack/react-query",
		version: "5.100",
		license: "MIT",
		homepageUrl: "https://tanstack.com/query",
	},
	{
		id: "tanstack-router",
		name: "@tanstack/react-router",
		version: "1.70",
		license: "MIT",
		homepageUrl: "https://tanstack.com/router",
	},
	{ id: "zustand", name: "zustand", version: "5.0", license: "MIT", homepageUrl: "https://github.com/pmndrs/zustand" },
	{ id: "axios", name: "axios", version: "1.16", license: "MIT", homepageUrl: "https://axios-http.com" },
	{
		id: "framer-motion",
		name: "framer-motion",
		version: "12.38",
		license: "MIT",
		homepageUrl: "https://www.framer.com/motion",
	},
];
