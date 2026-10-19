using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BannerlordEnvironmentManager.Companion;

// Hand rolled rather than taken from a package: the companion is loaded into the user's game, and
// every assembly it drags in is one more chance to change what the dry run is measuring.
internal sealed class JsonWriter
{
    private readonly StringBuilder _builder = new StringBuilder();

    private bool _needsComma;

    public JsonWriter BeginObject()
    {
        Separate();
        _builder.Append('{');
        _needsComma = false;
        return this;
    }

    public JsonWriter EndObject()
    {
        _builder.Append('}');
        _needsComma = true;
        return this;
    }

    public JsonWriter BeginArray(string name)
    {
        Name(name);
        _builder.Append('[');
        _needsComma = false;
        return this;
    }

    public JsonWriter EndArray()
    {
        _builder.Append(']');
        _needsComma = true;
        return this;
    }

    public JsonWriter Text(string name, string? value)
    {
        Name(name);

        if (value is null)
            _builder.Append("null");
        else
            AppendQuoted(value);

        _needsComma = true;
        return this;
    }

    // A bare element inside an array the caller opened, rather than a named property.
    public JsonWriter Value(string value)
    {
        Separate();
        AppendQuoted(value);
        _needsComma = true;
        return this;
    }

    public JsonWriter Number(string name, long value)
    {
        Name(name);
        _builder.Append(value.ToString(CultureInfo.InvariantCulture));
        _needsComma = true;
        return this;
    }

    public JsonWriter Number(string name, long? value)
    {
        if (value.HasValue)
            return Number(name, value.Value);

        Name(name);
        _builder.Append("null");
        _needsComma = true;
        return this;
    }

    public JsonWriter Boolean(string name, bool value)
    {
        Name(name);
        _builder.Append(value ? "true" : "false");
        _needsComma = true;
        return this;
    }

    public JsonWriter Strings(string name, IEnumerable<string> values)
    {
        BeginArray(name);

        foreach (var value in values)
        {
            Separate();
            AppendQuoted(value);
            _needsComma = true;
        }

        return EndArray();
    }

    public override string ToString() => _builder.ToString();

    private void Name(string name)
    {
        Separate();
        AppendQuoted(name);
        _builder.Append(':');
        _needsComma = false;
    }

    private void Separate()
    {
        if (_needsComma)
            _builder.Append(',');

        _needsComma = false;
    }

    private void AppendQuoted(string value)
    {
        _builder.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '"': _builder.Append("\\\""); break;
                case '\\': _builder.Append("\\\\"); break;
                case '\b': _builder.Append("\\b"); break;
                case '\f': _builder.Append("\\f"); break;
                case '\n': _builder.Append("\\n"); break;
                case '\r': _builder.Append("\\r"); break;
                case '\t': _builder.Append("\\t"); break;
                default:
                    if (c < ' ' || c > '~')
                        _builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        _builder.Append(c);
                    break;
            }
        }

        _builder.Append('"');
    }
}
