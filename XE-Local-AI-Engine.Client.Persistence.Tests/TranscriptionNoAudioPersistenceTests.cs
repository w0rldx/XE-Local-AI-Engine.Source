namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Reflection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     R12's architecture fence: a transcription session records what was said, never what was heard. No entity in the
///     family may grow a member that carries audio.
///     <para>
///         The property-type check is an allow-list rather than a "not <c>byte[]</c>" check on purpose. Audio does not
///         only arrive as <c>byte[]</c>: a <c>Memory&lt;byte&gt;</c>, a <c>Stream</c>, a <c>byte[][]</c> of frames or a
///         float sample buffer would all sail past a single-type ban. Anything outside the listed shapes fails here and
///         has to be argued for, which is the point.
///     </para>
///     <para>
///         It lives in this project rather than <c>XE-Local-AI-Engine.Tests</c> because both entities are
///         <c>internal</c> and <c>Properties/AssemblyInfo.cs</c> grants <c>InternalsVisibleTo</c> only to
///         <c>Client.Application</c> and this test project. Here the test names the types directly, so a renamed entity
///         is a compile error; a string-keyed <c>assembly.GetTypes()</c> scan elsewhere would simply go green.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class TranscriptionNoAudioPersistenceTests
{
    private static readonly string[] AudioBearingFragments = ["audio", "pcm", "waveform", "samples"];

    /// <summary>The five encrypted text columns — the only binary members these entities may ever hold.</summary>
    private static readonly HashSet<string> AllowedBinaryMembers = new(StringComparer.Ordinal)
    {
        nameof(TranscriptionSession.Title),
        nameof(TranscriptionSession.ConfigJson),
        nameof(TranscriptionSession.ErrorCode),
        nameof(TranscriptionSession.ErrorMessage),
        nameof(TranscriptSegment.Text)
    };

    [Test]
    public void TranscriptionEntities_ExposeNoAudioBearingMember()
    {
        foreach (var entityType in new[]
                 {
                     typeof(TranscriptionSession),
                     typeof(TranscriptSegment)
                 })
        {
            foreach (var property in entityType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                // `protected virtual Type EqualityContract` is emitted by the compiler for every record and describes
                // no column. Skipping it by name keeps the non-public sweep, which is what would catch a private
                // backing member EF had been told to map.
                if (string.Equals(property.Name, "EqualityContract", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var fragment in AudioBearingFragments)
                {
                    AssertEx.False(property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                        $"{entityType.Name}.{property.Name} names audio. A transcription session persists the transcript, never the recording (R12).");
                }

                AssertEx.True(IsAllowedShape(property),
                    $"{entityType.Name}.{property.Name} is a {property.PropertyType} — a shape this entity family does not allow. "
                    + "Scalars, strings, enums, Guids, the five encrypted text columns and the Segments navigation are the whole permitted set; "
                    + "anything else is how audio gets onto disk (R12).");
            }
        }
    }

    private static bool IsAllowedShape(PropertyInfo property)
    {
        var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (propertyType == typeof(byte[]))
        {
            return AllowedBinaryMembers.Contains(property.Name);
        }

        // The one navigation. Named explicitly so a second collection of anything has to be argued for.
        if (propertyType == typeof(List<TranscriptSegment>))
        {
            return string.Equals(property.Name, nameof(TranscriptionSession.Segments), StringComparison.Ordinal);
        }

        return propertyType.IsEnum
               || propertyType == typeof(string)
               || propertyType == typeof(Guid)
               || propertyType == typeof(bool)
               || propertyType == typeof(int)
               || propertyType == typeof(long)
               || propertyType == typeof(double);
    }
}
