using System.Text.Json;

namespace AgentFrameworkGitHubCopilot;

/// <summary>
/// Normalizes a raw LLM JSON response to match an expected JSON Schema.
/// Handles common mismatches when the model returns structurally different
/// but semantically equivalent JSON (e.g., objects where strings are expected,
/// or strings where objects are expected).
/// </summary>
internal static class CopilotSdkJsonResponseNormalizer
{
    /// <summary>
    /// Attempts to normalize the raw response text to conform to the given JSON Schema.
    /// Returns the normalized JSON string, or <c>null</c> if normalization is not possible.
    /// </summary>
    internal static string? TryNormalize(string responseText, JsonElement schema)
    {
        try
        {
            using var responseDoc = JsonDocument.Parse(responseText);

            if (responseDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!schema.TryGetProperty("properties", out JsonElement schemaProps))
            {
                return null;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                NormalizeObject(writer, responseDoc.RootElement, schemaProps);
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static void NormalizeObject(Utf8JsonWriter writer, JsonElement response, JsonElement schemaProps)
    {
        writer.WriteStartObject();

        foreach (JsonProperty schemaProp in schemaProps.EnumerateObject())
        {
            writer.WritePropertyName(schemaProp.Name);

            JsonElement responseProp = FindProperty(response, schemaProp.Name);

            if (responseProp.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteNullValue();
                continue;
            }

            NormalizeValue(writer, responseProp, schemaProp.Value);
        }

        writer.WriteEndObject();
    }

    private static void NormalizeValue(Utf8JsonWriter writer, JsonElement value, JsonElement schema)
    {
        string schemaType = GetSchemaType(schema);

        if (schemaType == "array" && value.ValueKind == JsonValueKind.Array)
        {
            NormalizeArray(writer, value, schema);
        }
        else
        {
            value.WriteTo(writer);
        }
    }

    private static void NormalizeArray(Utf8JsonWriter writer, JsonElement array, JsonElement schema)
    {
        if (!schema.TryGetProperty("items", out JsonElement itemsSchema))
        {
            array.WriteTo(writer);
            return;
        }

        string expectedItemType = GetSchemaType(itemsSchema);

        writer.WriteStartArray();

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (expectedItemType == "string" && item.ValueKind == JsonValueKind.Object)
            {
                // Model returned object where string expected — extract best string value
                string? extracted = ExtractStringFromObject(item);
                if (extracted is not null)
                {
                    writer.WriteStringValue(extracted);
                }
                else
                {
                    writer.WriteNullValue();
                }
            }
            else if (expectedItemType == "object" && item.ValueKind == JsonValueKind.String)
            {
                // Model returned string where object expected — wrap using first schema property
                string? wrapPropName = GetFirstSchemaPropertyName(itemsSchema);
                if (wrapPropName is not null)
                {
                    writer.WriteStartObject();
                    writer.WriteString(wrapPropName, item.GetString());
                    writer.WriteEndObject();
                }
                else
                {
                    item.WriteTo(writer);
                }
            }
            else
            {
                item.WriteTo(writer);
            }
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Extracts a representative string from an object by trying common property names,
    /// then falling back to the first string-valued property.
    /// </summary>
    private static string? ExtractStringFromObject(JsonElement obj)
    {
        ReadOnlySpan<string> commonNames = ["name", "Name", "value", "Value", "title", "Title", "label", "Label"];

        foreach (string propName in commonNames)
        {
            if (obj.TryGetProperty(propName, out JsonElement prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }

        // Fall back to first string property
        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the first property name from an object schema's "properties" definition.
    /// Used to wrap a bare string into the expected object shape.
    /// </summary>
    private static string? GetFirstSchemaPropertyName(JsonElement itemsSchema)
    {
        if (itemsSchema.TryGetProperty("properties", out JsonElement props))
        {
            foreach (JsonProperty prop in props.EnumerateObject())
            {
                return prop.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a property in a JSON object by name (case-insensitive).
    /// </summary>
    private static JsonElement FindProperty(JsonElement obj, string name)
    {
        // Try exact match first (avoids enumeration)
        if (obj.TryGetProperty(name, out JsonElement exact))
        {
            return exact;
        }

        // Fall back to case-insensitive enumeration
        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return prop.Value;
            }
        }

        return default;
    }

    /// <summary>
    /// Extracts the non-null type from a JSON Schema "type" field,
    /// which can be a string ("array") or an array (["array", "null"]).
    /// </summary>
    private static string GetSchemaType(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out JsonElement typeEl))
        {
            return "unknown";
        }

        if (typeEl.ValueKind == JsonValueKind.String)
        {
            return typeEl.GetString() ?? "unknown";
        }

        if (typeEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in typeEl.EnumerateArray())
            {
                string? typeName = item.GetString();
                if (typeName is not null and not "null")
                {
                    return typeName;
                }
            }
        }

        return "unknown";
    }
}