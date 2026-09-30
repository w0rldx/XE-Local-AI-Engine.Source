import { LogoMark } from "@/core/layout/components/Logo/LogoMark";
import { LogoText } from "@/core/layout/components/Logo/LogoText";

export function LogoCombined() {
	return (
		<div className="flex flex-row items-center gap-1">
			<LogoMark className="h-8 w-auto" />
			<LogoText className="h-2.5 w-auto" />
		</div>
	);
}
