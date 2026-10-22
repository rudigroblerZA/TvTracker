---
name: codeatout-csharp-unit-testing
description: Creates focused .NET unit tests using the repository's established test framework and assertion libraries. Use when testing isolated C# behaviour.
---

# .NET Unit Testing

## Principles

- Discover the repository's test framework, assertion library, naming, and
  fixture conventions before adding tests.
- Place unit tests in a top-level `test` or `tests` directory, in projects that
  mirror the production assembly or feature being tested. Do not place unit
  tests under `src` unless an established repository convention requires it.
- Test one externally observable behaviour per test. Name tests for the
  scenario and expected outcome.
- Use explicit `// Arrange`, `// Act`, `// Assert` comments in each test to
  make the phases of the test clear and consistent.
- Arrange only the dependencies necessary for the behaviour. Use real values
  where simple and mock external boundaries only when required.
- Include edge cases, invalid inputs, cancellation, and error paths when they
  are part of the public contract.
- Keep tests deterministic, independent, and safe for parallel execution.
- Use the assertion library already established by the test project. Shouldly
  and AwesomeAssertions are approved choices; select one for a test project
  rather than mixing assertion styles.
- Use a mocking library such as NSubstitute only at external or behavioural
  boundaries. Assert calls only when the interaction is part of the observable
  contract, and do not use substitutes to duplicate implementation logic.

## Test support code

Keep reusable test builders, claims or identity creators, common assertions,
and narrow substitute-inspection helpers in a dedicated test-fixtures project
or shared test-support area. Keep feature-specific setup close to its tests,
avoid shared mutable state, and do not use reflection to test private or
protected implementation details.

## Data-driven tests

Use parameterized tests when the same behaviour should hold for a concise,
readable set of inputs. Use separate tests when setup or expected behaviour
meaningfully differs.

## Validate

Run the focused test project and avoid weakening assertions, skipping tests, or
changing production visibility solely to test implementation details.

## Test naming

- The name of your test method should consist of three parts:
  1. The name of the method being tested: clearly indicate which method or
    functionality you are testing.
  2. The scenario under which it’s being tested: describe the specific
    scenario or input conditions for the test.
  3. The expected behavior when the scenario is invoked: state the outcome
    you expect from the method.

For example:

```csharp
public class CalculatorTests
{
   [Fact]
   public void Add_TwoPositiveNumbers_ReturnsCorrectSum()
   {
      // Arrange
      var calculator = new Calculator();
      // Act
      var result = calculator.Add(3, 5);
      // Assert
      Assert.Equal(8, result);
   }
}
```
