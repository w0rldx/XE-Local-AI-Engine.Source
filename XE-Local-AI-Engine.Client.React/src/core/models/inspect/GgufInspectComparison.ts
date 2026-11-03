import type {
	XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufInspectionResponse,
	XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufTensorDto,
} from "@/core/api/generated";

type TensorDto = XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufTensorDto;
type InspectionTensors = Pick<
	XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufInspectionResponse,
	"blocks" | "nonBlockTensors"
>;

/** One tensor name across two files; a side is null when that file does not have it. */
export interface ComparedTensor {
	name: string;
	a: TensorDto | null;
	b: TensorDto | null;
	typeChanged: boolean;
}

/** A sum is null when a tensor it covers has an unknown size, so a difference is never made up from a partial sum. */
export interface ComparedGroup {
	// Null for the tensors outside any block.
	blockIndex: number | null;
	bytesA: number | null;
	bytesB: number | null;
	difference: number | null;
	// Only what differs: a type or size change, or a tensor found in one file.
	changed: ComparedTensor[];
}

export interface GgufComparison {
	bytesA: number | null;
	bytesB: number | null;
	difference: number | null;
	changedTypeCount: number;
	onlyInACount: number;
	onlyInBCount: number;
	groups: ComparedGroup[];
}

interface Located {
	tensor: TensorDto;
	blockIndex: number | null;
}

function locate(inspection: InspectionTensors): Map<string, Located> {
	const located = new Map<string, Located>();
	for (const tensor of inspection.nonBlockTensors) {
		located.set(tensor.name, { tensor, blockIndex: null });
	}
	for (const block of inspection.blocks) {
		for (const tensor of block.tensors) {
			located.set(tensor.name, { tensor, blockIndex: block.index });
		}
	}
	return located;
}

function addBytes(sum: number | null, tensor: TensorDto | null): number | null {
	if (tensor === null) {
		return sum;
	}
	return sum === null || tensor.byteSize === null ? null : sum + tensor.byteSize;
}

function differenceOf(bytesA: number | null, bytesB: number | null): number | null {
	return bytesA === null || bytesB === null ? null : bytesB - bytesA;
}

/** Matches two files' tensors by name. Groups are in file order: the tensors outside blocks, then each block by index. */
export function compareInspections(a: InspectionTensors, b: InspectionTensors): GgufComparison {
	const locatedA = locate(a);
	const locatedB = locate(b);
	const groups = new Map<number | null, ComparedGroup>();
	const comparison: GgufComparison = {
		bytesA: 0,
		bytesB: 0,
		difference: null,
		changedTypeCount: 0,
		onlyInACount: 0,
		onlyInBCount: 0,
		groups: [],
	};

	for (const name of new Set([...locatedA.keys(), ...locatedB.keys()])) {
		const sideA = locatedA.get(name) ?? null;
		const sideB = locatedB.get(name) ?? null;
		const tensorA = sideA?.tensor ?? null;
		const tensorB = sideB?.tensor ?? null;
		const blockIndex = sideA?.blockIndex ?? sideB?.blockIndex ?? null;
		const group = groups.get(blockIndex) ?? { blockIndex, bytesA: 0, bytesB: 0, difference: null, changed: [] };
		groups.set(blockIndex, group);

		group.bytesA = addBytes(group.bytesA, tensorA);
		group.bytesB = addBytes(group.bytesB, tensorB);
		comparison.bytesA = addBytes(comparison.bytesA, tensorA);
		comparison.bytesB = addBytes(comparison.bytesB, tensorB);

		const typeChanged = tensorA !== null && tensorB !== null && tensorA.ggmlTypeId !== tensorB.ggmlTypeId;
		comparison.changedTypeCount += typeChanged ? 1 : 0;
		comparison.onlyInACount += tensorB === null ? 1 : 0;
		comparison.onlyInBCount += tensorA === null ? 1 : 0;
		if (typeChanged || tensorA === null || tensorB === null || tensorA.byteSize !== tensorB.byteSize) {
			group.changed.push({ name, a: tensorA, b: tensorB, typeChanged });
		}
	}

	comparison.difference = differenceOf(comparison.bytesA, comparison.bytesB);
	comparison.groups = [...groups.values()]
		.map((group) => ({ ...group, difference: differenceOf(group.bytesA, group.bytesB) }))
		.sort((left, right) => (left.blockIndex ?? -1) - (right.blockIndex ?? -1));
	return comparison;
}
