namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Every production exception offering a <c>(string message, Exception innerException)</c> constructor really
///     preserves both, rather than being covered by compilation alone.
/// </summary>
/// <remarks>
///     Most of these constructors have no call site at all, in product code or in a test, so compilation alone was
///     covering them and one that dropped its inner exception would ship green. The scan reads the built assemblies
///     instead of a hand-kept list, so a new exception type is covered the moment it compiles, and the floors below
///     fail rather than report a vacuous pass if the scan ever stops finding them. The scan's own output is the count.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ExceptionInnerExceptionContractTests
{
    // Set well under what the scan reports today, so deleting a few exception types is not a red herring about the
    // floor while a scan that silently stops finding them still fails. The failure messages carry the live counts.
    private const int PassThroughConstructorFloor = 75;

    private const int ProductionAssemblyFloor = 15;

    private const string Message = "the outer message";

    // Test-support assemblies ship beside the production ones in the test output directory; they are not product
    // surface, so their exception types are out of scope here.
    private static readonly string[] NonProductionAssemblies =
    [
        "XE-Local-AI-Engine.Tests",
        "XE-Local-AI-Engine.Client.Testing",
        "XE-Local-AI-Engine.Testing.FakeOllama",
        "XE-Local-AI-Engine.Testing.FakeDocker"
    ];

    // Declaration order matters: the scan runs once here and both tests read it.
    private static readonly (IReadOnlyList<string> Assemblies, IReadOnlyList<Type> ExceptionTypes, IReadOnlyList<string> Failures) Scan = ScanProductionAssemblies();

    /// <summary>
    ///     Constructs the type with a message and an inner exception and reads both back off the instance.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(PassThroughConstructorTypes))]
    public void Constructor_TakingAMessageAndAnInnerException_PreservesBoth(Type exceptionType)
    {
        var constructor = AssertEx.NotNull(FindPassThroughConstructor(exceptionType));
        var inner = new InvalidOperationException("the cause");

        var constructed = (Exception)constructor.Invoke([Message, inner]);

        AssertEx.Equal(Message, constructed.Message, $"'{exceptionType.Name}' must hand the message to its base constructor unchanged");
        AssertEx.True(ReferenceEquals(inner, constructed.InnerException),
            $"'{exceptionType.Name}' must keep the inner exception instance it was handed, so the cause survives a rethrow");
    }

    /// <summary>
    ///     The scan itself: every production assembly's types loaded, and it found the constructors it is meant to.
    /// </summary>
    [Test]
    public void ProductionAssemblyScan_FindsThePassThroughConstructorsItIsMeantTo()
    {
        AssertEx.Empty(Scan.Failures,
            $"a production assembly's types could not be loaded, so the parameterized test above covered less than it claims: {string.Join("; ", Scan.Failures)}");

        AssertEx.True(Scan.Assemblies.Count >= ProductionAssemblyFloor,
            $"expected at least {ProductionAssemblyFloor} production assemblies beside the test binary, found {Scan.Assemblies.Count}: {string.Join(", ", Scan.Assemblies)}");

        var covered = PassThroughConstructorTypes().Count();
        AssertEx.True(covered >= PassThroughConstructorFloor,
            $"expected at least {PassThroughConstructorFloor} exception types declaring a (string, Exception) constructor, found {covered}");
    }

    /// <summary>Every production exception type that declares the pass-through constructor itself.</summary>
    public static IEnumerable<Func<Type>> PassThroughConstructorTypes()
    {
        foreach (var type in Scan.ExceptionTypes.Where(static type => FindPassThroughConstructor(type) is not null)
                                 .OrderBy(static type => type.FullName, StringComparer.Ordinal))
        {
            yield return () => type;
        }
    }

    /// <summary>
    ///     The constructor DECLARED on this type, never one inherited: a derived exception that does not offer the
    ///     pass-through is not covered by its base's, and invoking the base's would test the base twice.
    /// </summary>
    private static ConstructorInfo? FindPassThroughConstructor(Type exceptionType)
    {
        return exceptionType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(string), typeof(Exception)],
            modifiers: null);
    }

    private static (IReadOnlyList<string> Assemblies, IReadOnlyList<Type> ExceptionTypes, IReadOnlyList<string> Failures) ScanProductionAssemblies()
    {
        var assemblyNames = new List<string>();
        var exceptionTypes = new List<Type>();
        var failures = new List<string>();

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "XE-Local-AI-Engine.*.dll")
                                      .OrderBy(static path => path, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (NonProductionAssemblies.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(path);
            }
            catch (Exception exception) when (exception is BadImageFormatException or FileLoadException)
            {
                failures.Add($"{name} could not be loaded ({exception.GetType().Name})");
                continue;
            }

            assemblyNames.Add(name);
            exceptionTypes.AddRange(ExceptionTypesOf(assembly, name, failures));
        }

        return (assemblyNames, exceptionTypes, failures);
    }

    private static IEnumerable<Type> ExceptionTypesOf(Assembly assembly, string assemblyName, List<string> failures)
    {
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // Partial load: keep what resolved and report the gap, so a new unresolvable type is visible rather than
            // silently narrowing what the parameterized test covers.
            failures.Add($"{assemblyName} loaded {exception.Types.Count(static type => type is not null)} of {exception.Types.Length} types");
            types = exception.Types;
        }

        return types.Where(static type => type is not null
                                          && !type.IsAbstract
                                          && typeof(Exception).IsAssignableFrom(type))!;
    }
}
