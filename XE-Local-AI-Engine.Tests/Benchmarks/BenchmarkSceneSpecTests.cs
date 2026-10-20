namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Scene schema v1 as a pure parser: defaults, caps, refusals and the axis-aligned bounds every relation is
///     decided on. The verdict built on it is covered by <see cref="BenchmarkJudgeVerifiersTests" />.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BenchmarkSceneSpecTests
{
    [Test]
    public void TryParse_DefaultsTheOmittedRotationScaleAndColor()
    {
        var parsed = BenchmarkSceneSpec.TryParse("""{"version":1,"objects":[{"id":"a","primitive":"box","position":[1,2,3]}]}""",
            out var scene, out var reason);

        AssertEx.True(parsed, reason);
        var only = AssertEx.NotNull(scene).Objects.Single();
        AssertEx.Equal(Vector(1, 2, 3), only.Position);
        AssertEx.Equal(Vector(0, 0, 0), only.Rotation);
        AssertEx.Equal(Vector(1, 1, 1), only.Scale);
        AssertEx.Equal(BenchmarkSceneSpec.DefaultColor, only.Color);
    }

    [Test]
    public void TryParse_KeepsEveryStatedMember()
    {
        var parsed = BenchmarkSceneSpec.TryParse(
            """{"version":1,"objects":[{"id":"a","primitive":"cone","position":[0,0,0],"rotation":[90,-360,360],"scale":[2,0.5,1000],"color":"#A0b1C2"}]}""",
            out var scene, out var reason);

        AssertEx.True(parsed, reason);
        var only = AssertEx.NotNull(scene).Objects.Single();
        AssertEx.Equal(Vector(90, -360, 360), only.Rotation);
        AssertEx.Equal(Vector(2, 0.5, 1000), only.Scale);
        AssertEx.Equal("#A0b1C2", only.Color);
    }

    [Test]
    public void TryParse_RefusesEveryViolationOfTheClosedSchema_NamingIt()
    {
        static string Scene(string objectJson) => $$"""{"version":1,"objects":[{{objectJson}}]}""";
        const string Valid = """{"id":"a","primitive":"box","position":[0,0,0]}""";
        var tooMany = string.Join(',', Enumerable.Range(0, BenchmarkSceneSpec.MaximumObjects + 1)
                                                 .Select(static index => $$"""{"id":"o{{index}}","primitive":"box","position":[0,0,0]}"""));

        Refused("not json", "not valid JSON");
        Refused("[]", "JSON object");
        Refused($$"""{"version":2,"objects":[{{Valid}}]}""", "version must be 1");
        Refused($$"""{"version":"1","objects":[{{Valid}}]}""", "version must be 1");
        Refused($$"""{"version":1,"objects":[{{Valid}}],"camera":1}""", "'camera'");
        Refused("""{"version":1,"objects":[]}""", "1 to 64");
        Refused($$"""{"version":1,"objects":[{{tooMany}}]}""", "1 to 64");
        Refused(Scene("7"), "object 1 must be a JSON object");
        Refused(Scene("""{"id":"a","primitive":"box","position":[0,0,0],"texture":"x.png"}"""), "unknown member 'texture'");
        Refused(Scene("""{"id":"a","id":"b","primitive":"box","position":[0,0,0]}"""), "'id' is repeated");
        Refused(Scene("""{"id":"","primitive":"box","position":[0,0,0]}"""), "'id'");
        Refused(Scene($$"""{"id":"{{new string('x', BenchmarkSceneSpec.MaximumIdLength + 1)}}","primitive":"box","position":[0,0,0]}"""), "'id'");
        Refused(Scene("""{"id":"a","primitive":"Box","position":[0,0,0]}"""), "'primitive'");
        Refused(Scene("""{"id":"a","primitive":"torus","position":[0,0,0]}"""), "'primitive'");
        Refused(Scene("""{"id":"a","primitive":"box","position":[0,0]}"""), "'position'");
        Refused(Scene("""{"id":"a","primitive":"box","position":[0,0,1000.5]}"""), "'position'");
        Refused(Scene("""{"id":"a","primitive":"box","position":[0,"0",0]}"""), "'position'");
        Refused(Scene("""{"id":"a","primitive":"box","position":[0,0,0],"rotation":[0,361,0]}"""), "'rotation'");
        Refused(Scene("""{"id":"a","primitive":"box","position":[0,0,0],"scale":[1,0,1]}"""), "'scale'");
        Refused(Scene("""{"id":"a","primitive":"box","position":[0,0,0],"scale":[1,1,1001]}"""), "'scale'");
        Refused(Scene("""{"id":"a","primitive":"box","position":[0,0,0],"color":"red"}"""), "'color'");
        Refused(Scene("""{"id":"a","primitive":"box","position":[0,0,0],"color":"#12345g"}"""), "'color'");
        Refused(Scene("""{"id":"a","primitive":"box"}"""), "missing 'position'");
        Refused(Scene("""{"primitive":"box","position":[0,0,0]}"""), "missing 'id'");
        Refused($$"""{"version":1,"objects":[{{Valid}},{{Valid}}]}""", "object 2: the id 'a' is already used");
    }

    [Test]
    public void TryParse_AcceptsTheObjectCap()
    {
        var atCap = string.Join(',', Enumerable.Range(0, BenchmarkSceneSpec.MaximumObjects)
                                               .Select(static index => $$"""{"id":"o{{index}}","primitive":"box","position":[0,0,0]}"""));

        AssertEx.True(BenchmarkSceneSpec.TryParse($$"""{"version":1,"objects":[{{atCap}}]}""", out var scene, out var reason), reason);
        AssertEx.Equal(BenchmarkSceneSpec.MaximumObjects, AssertEx.NotNull(scene).Objects.Count);
    }

    [Test]
    public void Bounds_ScaleTheUnitPrimitiveAroundThePosition_AndAPlaneHasNoThickness()
    {
        foreach (var primitive in new[] { BenchmarkSceneSpec.Box, BenchmarkSceneSpec.Sphere, BenchmarkSceneSpec.Cylinder, BenchmarkSceneSpec.Cone })
        {
            var bounds = BenchmarkSceneSpec.Bounds(Object("o", primitive, position: Vector(1, 2, 3), scale: Vector(2, 4, 6)));

            AssertEx.Equal(Vector(0, 0, 0), bounds.Min, primitive);
            AssertEx.Equal(Vector(2, 4, 6), bounds.Max, primitive);
        }

        var plane = BenchmarkSceneSpec.Bounds(Object("floor", BenchmarkSceneSpec.Plane, position: Vector(0, 1, 0), scale: Vector(10, 5, 10)));

        AssertEx.Equal(Vector(-5, 1, -5), plane.Min);
        AssertEx.Equal(Vector(5, 1, 5), plane.Max);
    }

    [Test]
    public void Holds_DecidesEachRelationOnTheBounds_IgnoringRotation()
    {
        var floor = Object("floor", BenchmarkSceneSpec.Plane, position: Vector(0, 0, 0), scale: Vector(10, 1, 10));
        var ball = Object("ball", BenchmarkSceneSpec.Sphere, position: Vector(0, 0.5, 0), scale: Vector(1, 1, 1));
        var box = Object("box", BenchmarkSceneSpec.Box, position: Vector(-1, 0.5, 0), scale: Vector(1, 1, 1));
        var tilted = Object("tilted", BenchmarkSceneSpec.Box, position: Vector(-1, 0.5, 0), scale: Vector(1, 1, 1)) with
        {
            Rotation = Vector(0, 45, 0)
        };

        AssertEx.True(BenchmarkSceneSpec.Holds(BenchmarkSceneSpecConfigV1.RelationAbove, ball, floor), "Touching counts as above.");
        AssertEx.False(BenchmarkSceneSpec.Holds(BenchmarkSceneSpecConfigV1.RelationAbove, floor, ball));
        AssertEx.True(BenchmarkSceneSpec.Holds(BenchmarkSceneSpecConfigV1.RelationLeftOf, box, ball), "Touching counts as left of.");
        AssertEx.False(BenchmarkSceneSpec.Holds(BenchmarkSceneSpecConfigV1.RelationLeftOf, ball, box));
        AssertEx.True(BenchmarkSceneSpec.Holds(BenchmarkSceneSpecConfigV1.RelationLeftOf, tilted, ball), "Rotation does not move the bounds.");
        AssertEx.True(BenchmarkSceneSpec.Holds(BenchmarkSceneSpecConfigV1.RelationInside, ball, Object("room", BenchmarkSceneSpec.Box, position: Vector(0, 1, 0), scale: Vector(4, 2, 4))));
        AssertEx.False(BenchmarkSceneSpec.Holds(BenchmarkSceneSpecConfigV1.RelationInside, box, ball));
    }

    private static void Refused(string json, string expected)
    {
        AssertEx.False(BenchmarkSceneSpec.TryParse(json, out var scene, out var reason), $"Accepted: {json}");
        AssertEx.Null(scene);
        AssertEx.Contains(reason, expected);
    }

    private static BenchmarkSceneVector Vector(double x, double y, double z) =>
        new() { X = x, Y = y, Z = z };

    private static BenchmarkSceneObject Object(string id, string primitive, BenchmarkSceneVector position, BenchmarkSceneVector scale) =>
        new()
        {
            Id = id,
            Primitive = primitive,
            Position = position,
            Rotation = Vector(0, 0, 0),
            Scale = scale,
            Color = BenchmarkSceneSpec.DefaultColor
        };
}
