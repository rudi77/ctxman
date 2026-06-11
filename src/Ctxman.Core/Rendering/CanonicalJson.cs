using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ctxman.Core.Rendering;

/// <summary>
/// Kanonische JSON-Serialisierung und Content-Hashing für den deterministischen Render-Prefix
/// (Spec §4.6). Garantiert byte-identische Bytes für identischen Segment-Stand: Keys werden auf
/// jeder Ebene ordinal sortiert, Whitespace ist kompakt, Zahlen werden kultur-invariant und stabil
/// formatiert, Array-Reihenfolge bleibt erhalten (sie trägt Bedeutung — Message- und Block-Order).
/// </summary>
public static class CanonicalJson
{
    // System.Text.Json garantiert keine alphabetische Key-Reihenfolge über heterogene/anonyme
    // Objekte. Wir serialisieren daher zunächst neutral in einen JsonNode und re-emittieren ihn
    // mit rekursiv ordinal sortierten Keys. Number-Handling bleibt am Default (kanonische
    // Round-Trip-Repräsentation von System.Text.Json), Reads/Writes sind invariant.
    private static readonly JsonSerializerOptions ToNodeOptions = new()
    {
        // Keine Indentierung, kein Sortieren hier — das Sortieren erfolgt explizit unten.
        WriteIndented = false,
    };

    /// <summary>
    /// Serialisiert ein beliebiges Render-Objekt zu kanonischem JSON: auf jeder Verschachtelungs-
    /// ebene alphabetisch (ordinal) sortierte Property-Keys, kompakter Whitespace, stabile
    /// Zahlenformatierung, UTF-8, kultur-invariant. Array-Reihenfolge wird bewahrt. (Spec §4.6, I4)
    /// </summary>
    public static string Serialize(object value)
    {
        // Round-Trip über JsonNode entkoppelt von der Property-Reihenfolge der Quelle.
        JsonNode? node = JsonSerializer.SerializeToNode(value, ToNodeOptions);
        JsonNode? canonical = Canonicalize(node);

        using var stream = new MemoryStream();
        var writerOptions = new JsonWriterOptions
        {
            Indented = false,
            // Striktes Escaping ist deterministisch; kein Provider-spezifisches Relaxen.
            SkipValidation = true,
        };
        using (var writer = new Utf8JsonWriter(stream, writerOptions))
        {
            if (canonical is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                canonical.WriteTo(writer);
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// SHA-256 (hex, lowercase) über die kanonischen JSON-Bytes (UTF-8) des Static-Prefix
    /// (System-Prompt + Tool-Defs). Cache-Stabilität ist hierüber messbar. (Spec §6, I4)
    /// </summary>
    public static string ComputeCachePrefixHash(object staticPrefix)
    {
        string canonical = Serialize(staticPrefix);
        return Sha256Hex(Encoding.UTF8.GetBytes(canonical));
    }

    /// <summary>
    /// SHA-256 (hex, lowercase) des Content-Strings (UTF-8). Dient dem Planner zur kanonischen
    /// Sortierung der Static-Segmente nach <c>(source, kind, content_hash)</c>. (Spec §4.2, I4)
    /// </summary>
    public static string ContentHash(string content)
    {
        return Sha256Hex(Encoding.UTF8.GetBytes(content));
    }

    private static JsonNode? Canonicalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                // Properties ordinal nach Name sortieren — kultur-stabil. (Spec §4.6)
                var sorted = new JsonObject();
                foreach (var kvp in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    sorted[kvp.Key] = Canonicalize(kvp.Value);
                }
                return sorted;
            }
            case JsonArray array:
            {
                // Array-Reihenfolge MUSS erhalten bleiben (sie trägt Bedeutung). (Spec §4.6)
                var result = new JsonArray();
                foreach (var element in array)
                {
                    result.Add(Canonicalize(element));
                }
                return result;
            }
            default:
                // Skalar (string/number/bool/null): unveränderte, kultur-invariante Kopie.
                return node?.DeepClone();
        }
    }

    private static string Sha256Hex(byte[] bytes)
    {
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
