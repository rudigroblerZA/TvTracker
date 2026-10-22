---
name: codeatout-csharp-autofixture
description: Creates concise, deterministic C# unit-test data and test doubles with AutoFixture. Use when test setup is dominated by default object construction or substitute wiring.
---

# AutoFixture for C# Unit Tests

Apply `codeatout-csharp-unit-testing` and `codeatout-csharp-xunit` first. Use
AutoFixture to remove irrelevant default setup, not to obscure the inputs and
outcomes that define the behaviour under test.

## Discover first

 - Use explicit `// Arrange`, `// Act`, `// Assert` comments in each test to
    make the phases of the test clear and consistent.

Inspect existing test projects for `AutoFixture`, `AutoFixture.AutoNSubstitute`,
test fixture factories, builders, NSubstitute conventions, and assertion
libraries. Follow the installed package versions and existing customization
patterns. Do not add `AutoFixture.Xunit2`, a custom auto-data attribute, or a
second mocking library as an incidental test change.

Use a new `Fixture` for each test or each xUnit test-class instance. Do not
share a mutable static fixture across parallel tests. Keep reusable,
domain-specific builders in the test-fixtures project only when more than one
test project needs them.

## Creating focused data

Use `Create<T>` when all generated values are irrelevant. Use `Build<T>` to
make the values that affect the scenario explicit, and avoid generating
production-looking personal, financial, or policy data in tests.

```csharp
[Fact]
public void Calculate_WhenDiscountIsApplied_ReturnsReducedPremium()
{
    // Arrange
    var fixture = new Fixture();
    PricingRequest request = fixture.Build<PricingRequest>()
        .With(value => value.BasePremium, 100m)
        .With(value => value.DiscountPercentage, 10m)
        .Create();
    var sut = new PremiumCalculator();

    // Act
    decimal result = sut.Calculate(request);

    // Assert
    result.ShouldBe(90m);
}
```

Use `Without` or an explicit value to model null, empty, invalid, boundary, or
business-significant properties. Do not rely on generated data for a value the
assertion depends on.

```csharp
CustomerRequest request = fixture.Build<CustomerRequest>()
    .Without(value => value.EmailAddress)
    .Create();
```

## Auto-substituting dependencies

When `AutoFixture.AutoNSubstitute` is established in the test project, it can
create substitutes for constructor dependencies while the test explicitly
configures the dependency that drives the scenario. This preserves readable
arrangement code without manually constructing unrelated dependencies.

```csharp
[Fact]
public async Task Handle_WhenClockIsUnavailable_PropagatesTheFailure()
{
    var fixture = new Fixture()
        .Customize(new AutoNSubstituteCustomization());
    var clock = fixture.Freeze<IClock>();
    var expectedException = new TimeoutException();
    clock.GetUtcNowAsync(Arg.Any<CancellationToken>())
        .Returns<Task<DateTimeOffset>>(_ => throw expectedException);
    Handler sut = fixture.Create<Handler>();

    TimeoutException exception = await Should.ThrowAsync<TimeoutException>(
        () => sut.HandleAsync(CancellationToken.None));

    exception.ShouldBeSameAs(expectedException);
}
```

Use `Freeze<T>` only for a dependency whose exact instance must be configured
or asserted. Do not assert calls to every auto-created substitute; assert the
result or only the boundary interaction required by the public contract.

## Recursion and customizations

Configure recursion behaviour narrowly when object graphs contain cycles, and
keep the customization close to the tests that need it:

```csharp
var fixture = new Fixture();
fixture.Behaviors
    .OfType<ThrowingRecursionBehavior>()
    .ToList()
    .ForEach(behaviour => fixture.Behaviors.Remove(behaviour));
fixture.Behaviors.Add(new OmitOnRecursionBehavior());
```

Prefer a small explicit builder over a global customization when the variation
is feature-specific. Add a reusable customization only when it represents a
stable testing convention across multiple projects and still creates
synthetic, safe data.

## Validate

Run the focused test project. Confirm the generated data cannot make tests
non-deterministic, that explicit scenario values remain readable, and that
tests do not rely on AutoFixture's incidental defaults, production data, or
shared mutable fixture state.
