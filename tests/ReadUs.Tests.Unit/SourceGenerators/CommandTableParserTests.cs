using ReadUs.SourceGenerators;
using ReadUs.SourceGenerators.Model;

namespace ReadUs.Tests.Unit.SourceGenerators;

/// <summary>
/// Unit-level coverage of <see cref="CommandTableParser"/>'s argument-flattening rules
/// (project spec §3, §13 step 4) — see its own remarks for what each rule is. These use
/// small, self-contained JSON fixtures (not the vendored files) so they stay meaningful
/// even as codegen/redis-commands is refreshed to a newer Redis tag; end-to-end
/// coverage against the real vendored table lives in
/// ReadUs.Tests.Integration.GeneratedCommandsTests.
/// </summary>
public class CommandTableParserTests
{
    [Fact]
    public void ParsesASimpleRequiredKeyCommand()
    {
        const string json = """
            {
                "GET": {
                    "group": "string",
                    "arity": 2,
                    "command_flags": ["READONLY", "FAST"],
                    "key_specs": [
                        { "begin_search": { "index": { "pos": 1 } }, "find_keys": { "range": { "lastkey": 0, "step": 1, "limit": 0 } } }
                    ],
                    "arguments": [ { "name": "key", "type": "key" } ]
                }
            }
            """;

        var command = CommandTableParser.Parse(json);

        Assert.Equal("GET", command.Name);
        Assert.Null(command.Container);
        Assert.True(command.IsReadOnly);
        Assert.False(command.IsWrite);
        Assert.False(command.IsBlocking);
        Assert.False(command.HasUnknownKeys);
        Assert.Equal("GET", command.WireCommandName);

        Assert.NotNull(command.Parameters);
        var arg = Assert.IsType<CommandArgument>(Assert.Single(command.Parameters));
        Assert.Equal("key", arg.Name);
        Assert.Equal(ArgumentWireType.Bytes, arg.WireType);
        Assert.False(arg.Optional);
        Assert.False(arg.Multiple);

        var keySpec = Assert.Single(command.KeySpecs);
        Assert.Equal(1, keySpec.FirstKeyPosition);
        Assert.Equal(0, keySpec.LastKeyPosition);
    }

    [Fact]
    public void FlagsABlockingCommand()
    {
        const string json = """
            {
                "BLPOP": {
                    "group": "list",
                    "arity": -3,
                    "command_flags": ["WRITE", "BLOCKING"],
                    "key_specs": [
                        { "begin_search": { "index": { "pos": 1 } }, "find_keys": { "range": { "lastkey": -2, "step": 1, "limit": 0 } } }
                    ],
                    "arguments": [
                        { "name": "key", "type": "key", "multiple": true },
                        { "name": "timeout", "type": "double" }
                    ]
                }
            }
            """;

        var command = CommandTableParser.Parse(json);

        Assert.True(command.IsBlocking);
        Assert.True(command.IsWrite);

        var parameters = command.Parameters!;
        Assert.Equal(2, parameters.Count);
        var key = Assert.IsType<CommandArgument>(parameters[0]);
        Assert.True(key.Multiple);
        Assert.Equal(ArgumentWireType.Bytes, key.WireType);
        var timeout = Assert.IsType<CommandArgument>(parameters[1]);
        Assert.Equal(ArgumentWireType.Double, timeout.WireType);
        Assert.False(timeout.Multiple);
    }

    [Fact]
    public void FlattensATopLevelOneofIntoIndependentOptionalMembersPrefixedByItsName()
    {
        const string json = """
            {
                "EXPIRE": {
                    "group": "generic",
                    "arity": -3,
                    "command_flags": ["WRITE"],
                    "key_specs": [
                        { "begin_search": { "index": { "pos": 1 } }, "find_keys": { "range": { "lastkey": 0, "step": 1, "limit": 0 } } }
                    ],
                    "arguments": [
                        { "name": "key", "type": "key" },
                        { "name": "seconds", "type": "integer" },
                        {
                            "name": "condition",
                            "type": "oneof",
                            "optional": true,
                            "arguments": [
                                { "name": "nx", "type": "pure-token", "token": "NX" },
                                { "name": "xx", "type": "pure-token", "token": "XX" }
                            ]
                        }
                    ]
                }
            }
            """;

        var command = CommandTableParser.Parse(json);
        var parameters = command.Parameters!;

        Assert.Equal(4, parameters.Count);
        var nx = Assert.IsType<CommandArgument>(parameters[2]);
        Assert.Equal("condition-nx", nx.Name);
        Assert.Equal(ArgumentWireType.PureToken, nx.WireType);
        Assert.True(nx.Optional);
        Assert.Equal("NX", nx.Token);

        var xx = Assert.IsType<CommandArgument>(parameters[3]);
        Assert.Equal("condition-xx", xx.Name);
        Assert.True(xx.Optional);
    }

    [Fact]
    public void DerivesAPureTokensImplicitTokenFromItsOwnNameWhenNoneIsDeclared()
    {
        const string json = """
            {
                "SIMULATE-FAILURE": {
                    "group": "sentinel",
                    "arity": -3,
                    "container": "SENTINEL",
                    "command_flags": [],
                    "key_specs": [],
                    "arguments": [
                        {
                            "name": "mode",
                            "type": "oneof",
                            "optional": true,
                            "multiple": true,
                            "arguments": [
                                { "name": "crash-after-election", "type": "pure-token" }
                            ]
                        }
                    ]
                }
            }
            """;

        var command = CommandTableParser.Parse(json);

        Assert.Equal("SENTINEL", command.Container);
        Assert.Equal("SENTINEL", command.WireCommandName);

        var member = Assert.IsType<CommandArgument>(Assert.Single(command.Parameters!));
        Assert.Equal("CRASH-AFTER-ELECTION", member.Token);
    }

    [Fact]
    public void RequiredNonRepeatingBlockMembersAreInlinedAsOrdinaryRequiredParameters()
    {
        const string json = """
            {
                "EXAMPLE": {
                    "group": "generic",
                    "arity": 3,
                    "command_flags": [],
                    "key_specs": [],
                    "arguments": [
                        {
                            "name": "pair",
                            "type": "block",
                            "arguments": [
                                { "name": "a", "type": "string" },
                                { "name": "b", "type": "string" }
                            ]
                        }
                    ]
                }
            }
            """;

        var command = CommandTableParser.Parse(json);
        var parameters = command.Parameters!;

        Assert.Equal(2, parameters.Count);
        Assert.IsType<CommandArgument>(parameters[0]);
        Assert.IsType<CommandArgument>(parameters[1]);
        Assert.False(((CommandArgument)parameters[0]).Optional);
    }

    [Fact]
    public void OptionalNonRepeatingBlockBecomesOneGroupParameter()
    {
        const string json = """
            {
                "SORT": {
                    "group": "generic",
                    "arity": -2,
                    "command_flags": ["WRITE"],
                    "key_specs": [],
                    "arguments": [
                        { "name": "key", "type": "key" },
                        {
                            "token": "LIMIT",
                            "name": "limit",
                            "type": "block",
                            "optional": true,
                            "arguments": [
                                { "name": "offset", "type": "integer" },
                                { "name": "count", "type": "integer" }
                            ]
                        }
                    ]
                }
            }
            """;

        var command = CommandTableParser.Parse(json);
        var parameters = command.Parameters!;

        Assert.Equal(2, parameters.Count);
        var group = Assert.IsType<CommandArgumentGroup>(parameters[1]);
        Assert.Equal("LIMIT", group.Token);
        Assert.True(group.Optional);
        Assert.False(group.Multiple);
        Assert.Equal(2, group.Members.Count);
        Assert.All(group.Members, m => Assert.Equal(ArgumentWireType.Integer, m.WireType));
    }

    [Fact]
    public void RepeatingBlockBecomesOneListOfTuplesGroupParameter()
    {
        const string json = """
            {
                "HSET": {
                    "group": "hash",
                    "arity": -4,
                    "command_flags": ["WRITE"],
                    "key_specs": [],
                    "arguments": [
                        { "name": "key", "type": "key" },
                        {
                            "name": "data",
                            "type": "block",
                            "multiple": true,
                            "arguments": [
                                { "name": "field", "type": "string" },
                                { "name": "value", "type": "string" }
                            ]
                        }
                    ]
                }
            }
            """;

        var command = CommandTableParser.Parse(json);
        var parameters = command.Parameters!;

        var group = Assert.IsType<CommandArgumentGroup>(parameters[1]);
        Assert.True(group.Multiple);
        Assert.Null(group.Token);
        Assert.Equal(["field", "value"], group.Members.Select(m => m.Name));
    }

    [Fact]
    public void UnresolvableKeySpecSetsHasUnknownKeysWithoutFailingTheWholeCommand()
    {
        const string json = """
            {
                "SORT": {
                    "group": "generic",
                    "arity": -2,
                    "command_flags": ["WRITE"],
                    "key_specs": [
                        { "begin_search": { "index": { "pos": 1 } }, "find_keys": { "range": { "lastkey": 0, "step": 1, "limit": 0 } } },
                        { "begin_search": { "unknown": null }, "find_keys": { "unknown": null } }
                    ],
                    "arguments": [ { "name": "key", "type": "key" } ]
                }
            }
            """;

        var command = CommandTableParser.Parse(json);

        Assert.True(command.HasUnknownKeys);
        Assert.Single(command.KeySpecs);
    }

    [Fact]
    public void ANonScalarBlockMemberAbortsFlatteningForTheWholeCommand()
    {
        const string json = """
            {
                "WEIRD": {
                    "group": "generic",
                    "arity": 2,
                    "command_flags": [],
                    "key_specs": [],
                    "arguments": [
                        {
                            "name": "group",
                            "type": "block",
                            "arguments": [
                                { "name": "inner", "type": "oneof", "arguments": [ { "name": "a", "type": "pure-token", "token": "A" } ] }
                            ]
                        }
                    ]
                }
            }
            """;

        var command = CommandTableParser.Parse(json);

        Assert.Null(command.Parameters);
    }
}
