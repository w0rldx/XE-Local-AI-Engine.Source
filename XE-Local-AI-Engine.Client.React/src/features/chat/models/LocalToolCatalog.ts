export interface LocalToolDescriptor {
	readonly name: string;
	readonly description: string;
	readonly requiresApproval: boolean;
}

// Static catalog mirrors LocalAgentToolRegistry.GetLocalChatTools() on the backend.
// Names must match the registered AIFunction names exactly — display only, no execution here.
export const localToolCatalog: readonly LocalToolDescriptor[] = [
	{
		name: "GetCurrentTime",
		description: "Returns the current UTC and local time plus today's date. Accepts an optional timezone.",
		requiresApproval: false,
	},
	{
		name: "Calculate",
		description: "Evaluates basic arithmetic expressions (+ - * / and parentheses). Safe in-process parser — no code execution.",
		requiresApproval: false,
	},
];
