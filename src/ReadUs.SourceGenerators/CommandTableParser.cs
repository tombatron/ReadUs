using System.Collections.Generic;
using System.Linq;
using ReadUs.SourceGenerators.Json;
using ReadUs.SourceGenerators.Model;

namespace ReadUs.SourceGenerators;

/// <summary>
/// Turns one vendored command-table JSON file (project spec §3) into a
/// <see cref="CommandDefinition"/>. Every command/subcommand always gets a metadata
/// entry; only some get a typed <see cref="CommandDefinition.Parameters"/> shape.
///
/// The flattening rules for <c>arguments</c> (kept intentionally to two levels — a
/// plain command argument, or one level of <c>oneof</c>/<c>block</c> nesting beneath
/// it — never deeper):
/// <list type="bullet">
/// <item>A plain scalar/pure-token argument becomes one parameter directly.</item>
/// <item>A top-level <c>oneof</c> is flattened into independent optional parameters,
/// one per member — the server still enforces mutual exclusivity; this client doesn't
/// duplicate that validation (a documented, deliberate looseness).</item>
/// <item>A top-level <c>block</c> whose members are all plain scalars becomes: if
/// required and non-repeating, its members inlined directly as ordinary required
/// parameters (they're always present in a fixed sequence, so no wrapper is needed);
/// otherwise (optional and/or repeating) one <see cref="CommandArgumentGroup"/>
/// parameter (a nullable tuple, or a list of tuples for a repeating group).</item>
/// <item>Anything that doesn't fit this — a block/oneof nested inside another
/// block/oneof, or a block member that isn't a plain scalar — aborts flattening for
/// the <em>whole</em> command: it still gets a metadata entry, just no generated typed
/// method. Full argument-tree fidelity (matching every command Redis ships) is a much
/// bigger design task than this pass's scope; see docs/design/state-machines.md §5.</item>
/// </list>
/// </summary>
internal static class CommandTableParser
{
    public static CommandDefinition Parse(string json)
    {
        var root = JsonValue.Parse(json);
        var entry = root.EnumerateObject().First();
        string name = entry.Key;
        var body = entry.Value;

        var flags = new HashSet<string>(body.GetProperty("command_flags").EnumerateArray().Select(f => f.AsString()));

        var (keySpecs, hasUnknownKeys) = ParseKeySpecs(body.GetProperty("key_specs"));

        IReadOnlyList<ICommandParameterSlot>? parameters = null;
        if (body.HasProperty("arguments"))
        {
            parameters = TryFlattenArguments(body.GetProperty("arguments"));
        }
        else
        {
            parameters = [];
        }

        return new CommandDefinition
        {
            Name = name,
            Container = body.HasProperty("container") ? body.GetProperty("container").AsString() : null,
            Group = body.HasProperty("group") ? body.GetProperty("group").AsString() : "generic",
            Arity = body.GetProperty("arity").AsInt32(),
            IsWrite = flags.Contains("WRITE"),
            IsReadOnly = flags.Contains("READONLY"),
            IsBlocking = flags.Contains("BLOCKING"),
            KeySpecs = keySpecs,
            HasUnknownKeys = hasUnknownKeys,
            Parameters = parameters,
        };
    }

    private static (IReadOnlyList<KeySpec> Specs, bool HasUnknown) ParseKeySpecs(JsonValue keySpecsNode)
    {
        var specs = new List<KeySpec>();
        bool hasUnknown = false;

        foreach (var spec in keySpecsNode.EnumerateArray())
        {
            var beginSearch = spec.GetProperty("begin_search");
            var findKeys = spec.GetProperty("find_keys");

            if (beginSearch.HasProperty("index") && findKeys.HasProperty("range"))
            {
                var range = findKeys.GetProperty("range");
                specs.Add(new KeySpec
                {
                    FirstKeyPosition = beginSearch.GetProperty("index").GetProperty("pos").AsInt32(),
                    LastKeyPosition = range.GetProperty("lastkey").AsInt32(),
                    KeyStep = range.GetProperty("step").AsInt32(),
                });
            }
            else
            {
                // "keyword" begin_search (find by a named token) or "unknown" (client
                // can't determine key positions without server-side get-keys logic,
                // e.g. SORT's BY/GET/STORE) — not staticly resolvable here.
                hasUnknown = true;
            }
        }

        return (specs, hasUnknown);
    }

    private static List<ICommandParameterSlot>? TryFlattenArguments(JsonValue argumentsNode)
    {
        var slots = new List<ICommandParameterSlot>();

        foreach (var arg in argumentsNode.EnumerateArray())
        {
            string type = arg.GetProperty("type").AsString();

            switch (type)
            {
                case "oneof":
                    // Prefixed with the oneof's own name: two independent oneof groups
                    // in the same command can otherwise produce identically-named
                    // members (e.g. BLMOVE's "wherefrom"/"whereto", each offering
                    // LEFT/RIGHT) that would collide as parameters once flattened to
                    // this same level.
                    string oneofName = arg.HasProperty("name") ? arg.GetProperty("name").AsString() : string.Empty;
                    foreach (var member in arg.GetProperty("arguments").EnumerateArray())
                    {
                        if (!TryResolveLeaf(member, forceOptional: true, out var flattenedMember, namePrefix: oneofName))
                        {
                            return null;
                        }

                        slots.Add(flattenedMember);
                    }

                    break;

                case "block":
                    var memberLeaves = new List<CommandArgument>();
                    foreach (var member in arg.GetProperty("arguments").EnumerateArray())
                    {
                        // Pure-token members don't fit the tuple-shaped emission a
                        // block group gets (see TypedMethodEmitter) — bail rather than
                        // special-case a shape that doesn't occur in practice.
                        if (!TryResolveLeaf(member, forceOptional: false, out var leaf) || leaf.Multiple || leaf.WireType == ArgumentWireType.PureToken)
                        {
                            return null;
                        }

                        memberLeaves.Add(leaf);
                    }

                    bool groupOptional = arg.HasProperty("optional") && arg.GetProperty("optional").AsBool();
                    bool groupMultiple = arg.HasProperty("multiple") && arg.GetProperty("multiple").AsBool();

                    if (!groupOptional && !groupMultiple)
                    {
                        // Required, fixed-shape group: always present, so its members
                        // can just be inlined as ordinary required parameters in order.
                        slots.AddRange(memberLeaves);
                    }
                    else
                    {
                        slots.Add(new CommandArgumentGroup
                        {
                            Name = arg.GetProperty("name").AsString(),
                            Token = arg.HasProperty("token") ? arg.GetProperty("token").AsString() : null,
                            Optional = groupOptional,
                            Multiple = groupMultiple,
                            Members = memberLeaves,
                        });
                    }

                    break;

                default:
                    if (!TryResolveLeaf(arg, forceOptional: false, out var leaf2))
                    {
                        return null;
                    }

                    slots.Add(leaf2);
                    break;
            }
        }

        return slots;
    }

    private static bool TryResolveLeaf(JsonValue arg, bool forceOptional, out CommandArgument argument, string? namePrefix = null)
    {
        string type = arg.GetProperty("type").AsString();

        ArgumentWireType? wireType = type switch
        {
            "key" or "string" or "pattern" or "type" => ArgumentWireType.Bytes,
            "integer" or "unix-time" => ArgumentWireType.Integer,
            "double" => ArgumentWireType.Double,
            "pure-token" => ArgumentWireType.PureToken,
            _ => null,
        };

        if (wireType is null)
        {
            argument = null!;
            return false;
        }

        string rawName = arg.GetProperty("name").AsString();
        string name = string.IsNullOrEmpty(namePrefix) ? rawName : $"{namePrefix}-{rawName}";

        // A pure-token's literal keyword is usually explicit ("token": "NX"), but a
        // handful (e.g. SENTINEL SIMULATE-FAILURE's mode flags) omit it — Redis's own
        // convention there is that the keyword is just the argument's own name,
        // upper-cased (e.g. "crash-after-election" -> "CRASH-AFTER-ELECTION").
        string? token = arg.HasProperty("token")
            ? arg.GetProperty("token").AsString()
            : wireType == ArgumentWireType.PureToken ? rawName.ToUpperInvariant() : null;

        argument = new CommandArgument
        {
            Name = name,
            WireType = wireType.Value,
            Token = token,
            Optional = forceOptional || (arg.HasProperty("optional") && arg.GetProperty("optional").AsBool()),
            Multiple = arg.HasProperty("multiple") && arg.GetProperty("multiple").AsBool(),
            MultipleToken = arg.HasProperty("multiple_token") && arg.GetProperty("multiple_token").AsBool(),
        };
        return true;
    }
}
