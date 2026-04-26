namespace XE_Local_AI_Engine.Testing.FakeOllama.Determinism
{
    using System.Buffers.Binary;
    using System.Security.Cryptography;
    using System.Text;

    public static class EmbeddingDeterminism
    {
        public static IReadOnlyList<double> EmbedDeterministic(string input, int dimensions)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            var messageHash = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan()[..8]);
            var baseValue = (double)(messageHash % 1_000_003UL) * 0.001;
            var vector = new double[dimensions];

            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = Math.Sin(baseValue + i * 0.01);
            }

            return vector;
        }
    }
}
