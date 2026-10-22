---
name: codeatout-roslyn-architecture-analyzers
description: Creates, tests, versions, and consumes Roslyn analyzers that enforce .NET architectural rules at build time. Use when codifying stable architecture conventions as compiler diagnostics.
---

# Roslyn Architecture Analyzers

Use a Roslyn analyzer for a stable, mechanically verifiable rule that should
produce editor and build feedback across a solution, such as a dependency
boundary, naming convention, or required type shape. Apply
`codeatout-architecture` first to establish that the rule reflects an intended
and reviewable architecture rather than an incidental implementation detail.

## Choose the enforcement mechanism

Use a Roslyn analyzer when the rule concerns source symbols, syntax, semantic
relationships, or compilation references and must run while developers edit
and build. Use an architecture test for broader assembly-level structure that
is acceptable to evaluate during tests. Do not enforce the same rule in both
places without a clear reason and single ownership.

Avoid analyzers for style preferences already covered by the compiler,
`.editorconfig`, or an existing well-maintained analyzer package. Keep rules
specific, explainable, deterministic, and narrow enough to avoid false
positives.

## Libraries

| Library | Purpose |
| --- | --- |
| `Microsoft.CodeAnalysis.CSharp` | Roslyn C# compiler APIs: syntax, symbols, compilations, diagnostics, and analyzer registration. |
| `Microsoft.CodeAnalysis.Analyzers` | Meta-analyzers that validate analyzer correctness, performance, and packaging conventions. |
| `System.Collections.Immutable` | Provides `ImmutableArray<T>`, required by Roslyn diagnostic APIs. |
| `TngTech.ArchUnitNET` and `TngTech.ArchUnitNET.xUnit` | Optional test-time architecture assertions; these do not create compiler diagnostics. |
| `AwesomeAssertions` or Shouldly | Optional assertion libraries for analyzer or architecture tests; use the repository's chosen test assertion style. |

Some projects reference unrelated application packages in the analyzer project.
For example, a logging-destructuring package that is not used by analyzer
source should not be treated as an analyzer dependency or copied into a new
analyzer project. Add only packages required by the analyzer or its tests, use
compatible versions managed by the repository, and mark analyzer implementation
dependencies as private assets where appropriate.

## Analyzer project and solution wiring

Target a framework compatible with the compiler hosts that will load the
analyzer—commonly `netstandard2.0` for broad Visual Studio and build-host
compatibility. Confirm the consuming SDK and compiler support before changing
the target.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <IsRoslynAnalyzer>true</IsRoslynAnalyzer>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.Analyzers"
                      PrivateAssets="all" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp"
                      PrivateAssets="all" />
    <PackageReference Include="System.Collections.Immutable"
                      PrivateAssets="all" />
  </ItemGroup>
</Project>
```

Reference the analyzer as an analyzer, not as a runtime or compile-time
assembly dependency. A central build file can apply it to all intended projects
while excluding the analyzer project itself:

```xml
<ItemGroup Condition="'$(MSBuildProjectName)' != 'Company.ArchitectureAnalyzers'">
  <ProjectReference Include="$(MSBuildThisFileDirectory)src\Company.ArchitectureAnalyzers\Company.ArchitectureAnalyzers.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

Scope the reference deliberately. Exclude generated projects, samples, tools,
or test projects only when the rule is not applicable to them; document every
exclusion.

## Diagnostic contract

Assign each diagnostic a unique, stable identifier and define a clear title,
message, category, default severity, and enabled-by-default value. Treat an
identifier and its meaning as a public compatibility contract: do not silently
repurpose a shipped ID or change its severity without a managed rollout.

Maintain release-tracking files for shipped and unshipped diagnostics where the
repository uses them. Record new IDs, severity, category, and rationale before
release so developers can understand a diagnostic and configure it
appropriately.

## Symbol analyzer example

Prefer semantic symbols over file paths, type-name substring checks, or text
searches. The following analyzer reports a type that exposes a public
`SectionName` constant without the agreed configuration suffix:

```csharp
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConfigurationSuffixAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_rule = new(
        id: "ARCH001",
        title: "Configuration suffix violation",
        messageFormat: "Configuration type '{0}' must end with 'Configuration'",
        category: "Architecture",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(s_rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        bool hasPublicStringSectionName = type.GetMembers()
            .OfType<IFieldSymbol>()
            .Any(static field =>
                field.Name == "SectionName" &&
                field.IsConst &&
                field.DeclaredAccessibility == Accessibility.Public &&
                field.Type.SpecialType == SpecialType.System_String);

        if (!hasPublicStringSectionName ||
            type.Name.EndsWith("Configuration", StringComparison.Ordinal))
        {
            return;
        }

        Location? location = type.Locations
            .FirstOrDefault(static candidate => candidate.IsInSource);
        context.ReportDiagnostic(Diagnostic.Create(s_rule, location ?? Location.None, type.Name));
    }
}
```

Use the narrowest applicable registration:

| Rule target | Registration |
| --- | --- |
| Type, member, parameter, or interface relationship | `RegisterSymbolAction` |
| Syntax construct needing no semantic model | `RegisterSyntaxNodeAction` |
| Whole-compilation project or assembly reference boundary | `RegisterCompilationAction` |
| Cross-symbol state collected during a compilation | `RegisterCompilationStartAction` with an end action |

Do not assume every symbol has a source location. Generated, metadata, and
error symbols require safe handling. Avoid allocation-heavy work, blocking
I/O, mutable static state, and non-thread-safe caches because analyzers run
concurrently in interactive environments.

## Dependency-boundary rules

For project or assembly rules, use `Compilation.AssemblyName`,
`ReferencedAssemblyNames`, symbol namespaces, and symbol equality rather than
checking absolute paths or repository-specific directory names. Keep allowed
dependencies in a reviewable rule map or analyzer configuration, then report
the consuming assembly and forbidden dependency in the diagnostic message.

Allow exceptions only for a documented boundary adapter. Make the exception
symbol-specific and test it, rather than suppressing the analyzer globally or
disabling the rule for an entire project.

## Test analyzers

Place analyzer tests under the repository's top-level `test` or `tests`
directory. Compile minimal source strings with Roslyn, execute the analyzer,
and assert diagnostic ID, severity, location, and message arguments. Include
both compliant and non-compliant source for each rule.

```csharp
[Fact]
public async Task Analyze_WhenSectionNameTypeLacksSuffix_ReportsArchitectureDiagnostic()
{
    CSharpCompilation compilation = CSharpCompilation.Create(
        assemblyName: "TestAssembly",
        syntaxTrees:
        [
            CSharpSyntaxTree.ParseText(
                "public sealed class Settings { public const string SectionName = \"Settings\"; }")
        ],
        references:
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
        ]);
    CompilationWithAnalyzers compilationWithAnalyzers = compilation.WithAnalyzers(
        ImmutableArray.Create<DiagnosticAnalyzer>(new ConfigurationSuffixAnalyzer()));

    ImmutableArray<Diagnostic> diagnostics =
        await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

    diagnostics.ShouldContain(diagnostic => diagnostic.Id == "ARCH001");
}
```

Test rule exclusions, generated or metadata-symbol safety, false-positive
boundaries, and compilations with missing references. Run the focused analyzer
test project and a representative consuming-project build to confirm MSBuild
wiring produces the expected diagnostic severity.
