using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using ReadUs.SourceGenerators.StringLiteralArgs;

namespace ReadUs.Tests.Unit.SourceGenerators;

// DefaultVerifier (test-framework-agnostic) rather than XUnitVerifier: the latter is
// obsolete upstream and, in practice, binary-incompatible with the xunit.assert version
// this project already uses elsewhere (a mismatched Xunit.Sdk.EqualException
// constructor) - DefaultVerifier sidesteps that entirely.
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixVerifier<StringLiteralArgsAnalyzer, StringLiteralArgsCodeFixProvider, DefaultVerifier>;
using TestHarness = Microsoft.CodeAnalysis.CSharp.Testing.CSharpCodeFixTest<StringLiteralArgsAnalyzer, StringLiteralArgsCodeFixProvider, DefaultVerifier>;

/// <summary>
/// Exercises <see cref="StringLiteralArgsAnalyzer"/>/<see cref="StringLiteralArgsCodeFixProvider"/>
/// via the real Roslyn testing verifiers — the fixed source is asserted exactly, not
/// just "no diagnostic remains", matching this project's own "let the compiler prove
/// it" discipline (see the pattern-variable bug the Hash generator's own tests caught
/// the same way).
/// </summary>
public class StringLiteralArgsAnalyzerTests
{
    [Fact]
    public async Task FlagsAndFixesAnAllLiteralCommandNameOnlyCall()
    {
        var test = """
            using ReadUs;

            class C
            {
                async System.Threading.Tasks.Task M(IRedisClient client)
                {
                    await {|#0:client.ExecuteAsync("PING")|};
                }
            }
            """;

        var fixedSource = """
            using ReadUs;

            class C
            {
                async System.Threading.Tasks.Task M(IRedisClient client)
                {
                    await client.ExecuteAsync("PING"u8.ToArray(), []);
                }
            }
            """;

        await VerifyAsync(test, fixedSource, Verify.Diagnostic(StringLiteralArgsAnalyzer.Rule).WithLocation(0).WithArguments("ExecuteAsync"));
    }

    [Fact]
    public async Task FlagsAndFixesAnAllLiteralCommandNameAndArgsCall()
    {
        var test = """
            using ReadUs;

            class C
            {
                async System.Threading.Tasks.Task M(IRedisClient client)
                {
                    await {|#0:client.ExecuteAsync("SET", ["key", "value"])|};
                }
            }
            """;

        var fixedSource = """
            using ReadUs;

            class C
            {
                async System.Threading.Tasks.Task M(IRedisClient client)
                {
                    await client.ExecuteAsync("SET"u8.ToArray(), ["key"u8.ToArray(), "value"u8.ToArray()]);
                }
            }
            """;

        await VerifyAsync(test, fixedSource, Verify.Diagnostic(StringLiteralArgsAnalyzer.Rule).WithLocation(0).WithArguments("ExecuteAsync"));
    }

    [Fact]
    public async Task DoesNotFlagACallWithANonLiteralArgument()
    {
        var test = """
            using ReadUs;

            class C
            {
                async System.Threading.Tasks.Task M(IRedisClient client, string key)
                {
                    await client.ExecuteAsync("GET", [key]);
                }
            }
            """;

        await VerifyAsync(test, test);
    }

    [Fact]
    public async Task DoesNotFlagACallAlreadyUsingTheByteOverload()
    {
        var test = """
            using ReadUs;

            class C
            {
                async System.Threading.Tasks.Task M(IRedisClient client)
                {
                    await client.ExecuteAsync("PING"u8.ToArray(), []);
                }
            }
            """;

        await VerifyAsync(test, test);
    }

    [Fact]
    public async Task RoundTripsAnEscapedQuoteInsideTheLiteral()
    {
        var test = """
            using ReadUs;

            class C
            {
                async System.Threading.Tasks.Task M(IRedisClient client)
                {
                    await {|#0:client.ExecuteAsync("say \"hi\"")|};
                }
            }
            """;

        var fixedSource = """
            using ReadUs;

            class C
            {
                async System.Threading.Tasks.Task M(IRedisClient client)
                {
                    await client.ExecuteAsync("say \"hi\""u8.ToArray(), []);
                }
            }
            """;

        await VerifyAsync(test, fixedSource, Verify.Diagnostic(StringLiteralArgsAnalyzer.Rule).WithLocation(0).WithArguments("ExecuteAsync"));
    }

    private static async Task VerifyAsync(string source, string fixedSource, params DiagnosticResult[] expected)
    {
        var test = new TestHarness
        {
            TestState =
            {
                Sources = { StubSource, source },
            },
            FixedState =
            {
                Sources = { StubSource, fixedSource },
            },
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };

        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }

    private const string StubSource = """
        namespace ReadUs
        {
            public interface IRedisClient : System.IAsyncDisposable
            {
            }
        }

        namespace ReadUs.Protocol
        {
            public readonly struct RedisResult
            {
            }
        }

        namespace ReadUs
        {
            public static class StringCommandExtensions
            {
                public static System.Threading.Tasks.ValueTask<ReadUs.Protocol.RedisResult> ExecuteAsync(this IRedisClient client, string commandName, string[]? args = null, System.Threading.CancellationToken cancellationToken = default) => default;

                public static System.Threading.Tasks.ValueTask<ReadUs.Protocol.RedisResult> ExecuteBlockingAsync(this IRedisClient client, string commandName, string[]? args = null, System.Threading.CancellationToken cancellationToken = default) => default;
            }

            public static class ByteCommandExtensions
            {
                public static System.Threading.Tasks.ValueTask<ReadUs.Protocol.RedisResult> ExecuteAsync(this IRedisClient client, System.ReadOnlyMemory<byte> commandName, System.ReadOnlyMemory<byte>[] args, System.Threading.CancellationToken cancellationToken = default) => default;
            }
        }
        """;
}
