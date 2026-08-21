// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2RMultiExport.Lib.Models;

/// <summary>
/// Writes a skill line's per-level value tables on a single line
/// (<c>[[3,4,5],[6,7,8]]</c>) instead of letting the pretty-printer put every number on its
/// own row. The keyed bundle is pretty-printed so humans can diff it, but these tables are
/// a few hundred thousand numbers across the whole export — indenting them would multiply
/// <c>skills.json</c> several times over for no readability gain.
/// </summary>
internal sealed class LevelTableConverter : JsonConverter<List<double[]>>
{
    public override List<double[]>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();

        var tables = new List<double[]>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            var table = new List<double>();
            while (reader.Read() && reader.TokenType == JsonTokenType.Number) table.Add(reader.GetDouble());
            tables.Add([.. table]);
        }
        return tables;
    }

    public override void Write(Utf8JsonWriter writer, List<double[]> value, JsonSerializerOptions options)
    {
        var builder = new StringBuilder("[");
        for (var table = 0; table < value.Count; table++)
        {
            if (table > 0) builder.Append(',');
            builder.Append('[');
            for (var level = 0; level < value[table].Length; level++)
            {
                if (level > 0) builder.Append(',');
                builder.Append(value[table][level].ToString("0.###", CultureInfo.InvariantCulture));
            }
            builder.Append(']');
        }
        builder.Append(']');

        writer.WriteRawValue(builder.ToString(), skipInputValidation: true);
    }
}
