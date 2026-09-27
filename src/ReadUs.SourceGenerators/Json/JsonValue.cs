using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ReadUs.SourceGenerators.Json;

internal enum JsonKind
{
    Object,
    Array,
    String,
    Number,
    Bool,
    Null,
}

/// <summary>
/// A minimal JSON document model, hand-rolled specifically to avoid taking a NuGet
/// dependency inside a source generator project — generator dependencies have to be
/// packaged for the compiler's isolated load context, which is easy to get subtly
/// wrong, whereas a ~150-line parser sufficient for the well-formed, non-exotic Redis
/// command-table JSON (project spec §3) has zero packaging risk. Not a general-purpose
/// JSON library; do not reuse it outside this project.
/// </summary>
internal sealed class JsonValue
{
    private static readonly Dictionary<string, JsonValue> EmptyObject = new();
    private static readonly List<JsonValue> EmptyArray = new();

    public static readonly JsonValue Null = new(JsonKind.Null);

    private readonly Dictionary<string, JsonValue>? _object;
    private readonly List<JsonValue>? _array;
    private readonly string? _string;
    private readonly double _number;
    private readonly bool _bool;

    private JsonValue(JsonKind kind, Dictionary<string, JsonValue>? obj = null, List<JsonValue>? array = null, string? str = null, double number = 0, bool boolean = false)
    {
        Kind = kind;
        _object = obj;
        _array = array;
        _string = str;
        _number = number;
        _bool = boolean;
    }

    public JsonKind Kind { get; }

    public static JsonValue Parse(string text) => new JsonParser(text).ParseDocument();

    public bool TryGetProperty(string name, out JsonValue value)
    {
        if (Kind == JsonKind.Object && _object!.TryGetValue(name, out var found))
        {
            value = found;
            return true;
        }

        value = Null;
        return false;
    }

    public JsonValue GetProperty(string name) => TryGetProperty(name, out var value) ? value : Null;

    public bool HasProperty(string name) => Kind == JsonKind.Object && _object!.ContainsKey(name);

    public IEnumerable<KeyValuePair<string, JsonValue>> EnumerateObject() => Kind == JsonKind.Object ? _object! : EmptyObject;

    public IReadOnlyList<JsonValue> EnumerateArray() => Kind == JsonKind.Array ? _array! : EmptyArray;

    public string AsString() => Kind == JsonKind.String ? _string! : string.Empty;

    public bool AsBool() => Kind == JsonKind.Bool && _bool;

    public double AsNumber() => Kind == JsonKind.Number ? _number : 0;

    public int AsInt32() => (int)AsNumber();

    private sealed class JsonParser(string text)
    {
        private readonly string _text = text;
        private int _pos;

        public JsonValue ParseDocument()
        {
            SkipWhitespace();
            var value = ParseValue();
            SkipWhitespace();
            return value;
        }

        private JsonValue ParseValue()
        {
            SkipWhitespace();
            var c = _text[_pos];
            return c switch
            {
                '{' => ParseObject(),
                '[' => ParseArray(),
                '"' => new JsonValue(JsonKind.String, str: ParseString()),
                't' => ParseLiteral("true", new JsonValue(JsonKind.Bool, boolean: true)),
                'f' => ParseLiteral("false", new JsonValue(JsonKind.Bool, boolean: false)),
                'n' => ParseLiteral("null", Null),
                _ => ParseNumber(),
            };
        }

        private JsonValue ParseObject()
        {
            var result = new Dictionary<string, JsonValue>();
            Expect('{');
            SkipWhitespace();
            if (Peek() == '}')
            {
                _pos++;
                return new JsonValue(JsonKind.Object, obj: result);
            }

            while (true)
            {
                SkipWhitespace();
                var key = ParseString();
                SkipWhitespace();
                Expect(':');
                var value = ParseValue();
                result[key] = value;
                SkipWhitespace();
                var next = _text[_pos++];
                if (next == '}')
                {
                    break;
                }

                if (next != ',')
                {
                    throw new FormatException($"Expected ',' or '}}' at position {_pos - 1} in {_text}");
                }
            }

            return new JsonValue(JsonKind.Object, obj: result);
        }

        private JsonValue ParseArray()
        {
            var result = new List<JsonValue>();
            Expect('[');
            SkipWhitespace();
            if (Peek() == ']')
            {
                _pos++;
                return new JsonValue(JsonKind.Array, array: result);
            }

            while (true)
            {
                result.Add(ParseValue());
                SkipWhitespace();
                var next = _text[_pos++];
                if (next == ']')
                {
                    break;
                }

                if (next != ',')
                {
                    throw new FormatException($"Expected ',' or ']' at position {_pos - 1} in {_text}");
                }

                SkipWhitespace();
            }

            return new JsonValue(JsonKind.Array, array: result);
        }

        private string ParseString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (true)
            {
                var c = _text[_pos++];
                if (c == '"')
                {
                    break;
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                var escape = _text[_pos++];
                switch (escape)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        var hex = _text.Substring(_pos, 4);
                        _pos += 4;
                        sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        break;
                    default:
                        throw new FormatException($"Unknown escape sequence '\\{escape}' at position {_pos - 1}");
                }
            }

            return sb.ToString();
        }

        private JsonValue ParseNumber()
        {
            var start = _pos;
            if (Peek() == '-')
            {
                _pos++;
            }

            while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] is '.' or 'e' or 'E' or '+' or '-'))
            {
                _pos++;
            }

            var token = _text.Substring(start, _pos - start);
            return new JsonValue(JsonKind.Number, number: double.Parse(token, CultureInfo.InvariantCulture));
        }

        private JsonValue ParseLiteral(string literal, JsonValue value)
        {
            if (string.CompareOrdinal(_text, _pos, literal, 0, literal.Length) != 0)
            {
                throw new FormatException($"Expected literal '{literal}' at position {_pos}");
            }

            _pos += literal.Length;
            return value;
        }

        private char Peek() => _text[_pos];

        private void Expect(char c)
        {
            if (_text[_pos] != c)
            {
                throw new FormatException($"Expected '{c}' at position {_pos} in {_text}");
            }

            _pos++;
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
            {
                _pos++;
            }
        }
    }
}
