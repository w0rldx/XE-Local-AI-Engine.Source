namespace XE_Local_AI_Engine.Tests.Workspace;

using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Where a reparse point's final target lands relative to a trusted root: the root and its descendants are inside; an ancestor, a
///     name-prefix sibling and a case variant are outside, the comparison being ordinal on every platform.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class HostPathSafetyTests
{
    [Test]
    public void TryResolveReparseWithinRoot_ForALinkToTheRootItself_IsWithin()
    {
        using var layout = new Layout();

        AssertEx.True(layout.Resolve(layout.Root, layout.Root));
    }

    [Test]
    public void TryResolveReparseWithinRoot_ForALinkToADescendant_IsWithin()
    {
        using var layout = new Layout();

        AssertEx.True(layout.Resolve(layout.InsideFile, layout.Root));
    }

    /// <summary>A root spelled with its trailing separator, as the file system root always is, still contains its descendants.</summary>
    [Test]
    public void TryResolveReparseWithinRoot_ForARootWithATrailingSeparator_ContainsItsDescendants()
    {
        using var layout = new Layout();

        AssertEx.True(layout.Resolve(layout.InsideFile, layout.Root + Path.DirectorySeparatorChar));
    }

    [Test]
    public void TryResolveReparseWithinRoot_ForALinkToANamePrefixSibling_IsOutside()
    {
        using var layout = new Layout();

        AssertEx.False(layout.Resolve(layout.SiblingFile, layout.Root), "'root-other' only starts with the root's name.");
    }

    [Test]
    public void TryResolveReparseWithinRoot_ForALinkToAnAncestor_IsOutside()
    {
        using var layout = new Layout();

        AssertEx.False(layout.Resolve(layout.Base, layout.Root));
    }

    /// <summary>Only a case-sensitive file system can hold both spellings as separate directories.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public void TryResolveReparseWithinRoot_ForALinkToACaseVariantOfTheRoot_IsOutside()
    {
        using var layout = new Layout();
        var variant = Directory.CreateDirectory(Path.Combine(layout.Base, "ROOT")).FullName;
        File.WriteAllText(Path.Combine(variant, "inside.txt"), "variant");

        AssertEx.False(layout.Resolve(Path.Combine(variant, "inside.txt"), layout.Root));
    }

    private sealed class Layout : IDisposable
    {
        private readonly TempDirectory _directory;

        public Layout()
        {
            SymlinkSupport.EnsureSupported();
            _directory = new TempDirectory("xe-host-path-safety");

            // Spelled the way HostPathSafety.ResolveTrustedRootOrNull spells a trusted root: links followed.
            Base = HostPathSafety.ResolveTrustedRootOrNull(_directory.Path) ?? _directory.Path;
            Root = Directory.CreateDirectory(Path.Combine(Base, "root")).FullName;
            InsideFile = Path.Combine(Root, "inside.txt");
            File.WriteAllText(InsideFile, "inside");
            var sibling = Directory.CreateDirectory(Path.Combine(Base, "root-other")).FullName;
            SiblingFile = Path.Combine(sibling, "outside.txt");
            File.WriteAllText(SiblingFile, "outside");
        }

        public string Base { get; }

        public string Root { get; }

        public string InsideFile { get; }

        public string SiblingFile { get; }

        public bool Resolve(string target, string root)
        {
            var link = Path.Combine(Base, $"link-{Guid.NewGuid():N}");
            var info = Directory.Exists(target)
                ? Directory.CreateSymbolicLink(link, target)
                : File.CreateSymbolicLink(link, target);

            AssertEx.True(HostPathSafety.TryResolveReparseWithinRoot(info, root, out var withinRoot), "The link target must resolve.");
            return withinRoot;
        }

        public void Dispose() =>
            _directory.Dispose();
    }
}
