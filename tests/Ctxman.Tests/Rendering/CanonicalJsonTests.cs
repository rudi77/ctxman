using Ctxman.Core.Rendering;

namespace Ctxman.Tests.Rendering;

/// <summary>
/// Tests des Determinismus-Vertrags der kanonischen JSON-Serialisierung (Spec §4.6, I4):
/// Keys auf jeder Ebene ordinal sortiert, Array-Reihenfolge bewahrt, kompakter Whitespace,
/// stabile Content-Hashes. Halten das Spec-Verhalten als Vertrag fest, nicht den Code-Pfad.
/// </summary>
public sealed class CanonicalJsonTests
{
    // ---- 1. Key-Reihenfolge ist kanonisch (alphabetisch/ordinal) ----

    [Fact] // Spec §4.6 I4: gleiche Inhalte, andere Deklarationsreihenfolge ⇒ byte-identisch.
    public void Serialize_SameContentDifferentKeyOrder_ProducesIdenticalString()
    {
        var a = CanonicalJson.Serialize(new { b = 1, a = 2 });
        var b = CanonicalJson.Serialize(new { a = 2, b = 1 });

        Assert.Equal(a, b);
    }

    [Fact] // Spec §4.6 I4: Keys erscheinen alphabetisch — "a" vor "b".
    public void Serialize_OrdersKeysAlphabetically()
    {
        var json = CanonicalJson.Serialize(new { b = 1, a = 2 });

        Assert.Equal("{\"a\":2,\"b\":1}", json);
    }

    [Fact] // Spec §4.6 I4: ordinale Sortierung auch über mehrere Keys hinweg stabil.
    public void Serialize_OrdersMultipleKeysOrdinally()
    {
        var json = CanonicalJson.Serialize(new { zebra = 1, apple = 2, mango = 3 });

        Assert.Equal("{\"apple\":2,\"mango\":3,\"zebra\":1}", json);
    }

    // ---- 2. Rekursive Key-Sortierung (verschachtelt + in Arrays) ----

    [Fact] // Spec §4.6 I4: verschachtelte Objekte werden ebenfalls sortiert.
    public void Serialize_SortsKeysOfNestedObjects()
    {
        var json = CanonicalJson.Serialize(new { outer = new { d = 1, c = 2 }, a = 3 });

        Assert.Equal("{\"a\":3,\"outer\":{\"c\":2,\"d\":1}}", json);
    }

    [Fact] // Spec §4.6 I4: Objekte innerhalb von Arrays werden ebenfalls sortiert.
    public void Serialize_SortsKeysOfObjectsInsideArrays()
    {
        var json = CanonicalJson.Serialize(new
        {
            items = new object[]
            {
                new { y = 1, x = 2 },
                new { b = 3, a = 4 },
            },
        });

        Assert.Equal("{\"items\":[{\"x\":2,\"y\":1},{\"a\":4,\"b\":3}]}", json);
    }

    [Fact] // Spec §4.6 I4: gleiche Inhalte mit anderer Key-Reihenfolge auf jeder Ebene ⇒ identisch.
    public void Serialize_DeeplyNested_SameContentDifferentOrder_ProducesIdenticalString()
    {
        var a = CanonicalJson.Serialize(new
        {
            z = new { q = new object[] { new { n = 1, m = 2 } }, p = 5 },
            a = 9,
        });
        var b = CanonicalJson.Serialize(new
        {
            a = 9,
            z = new { p = 5, q = new object[] { new { m = 2, n = 1 } } },
        });

        Assert.Equal(a, b);
    }

    // ---- 3. Array-Reihenfolge wird bewahrt (Arrays werden NICHT sortiert) ----

    [Fact] // Spec §4.6 I4: Array-Reihenfolge trägt Bedeutung und bleibt erhalten.
    public void Serialize_PreservesArrayElementOrder()
    {
        var json = CanonicalJson.Serialize(new { values = new[] { 3, 1, 2 } });

        Assert.Equal("{\"values\":[3,1,2]}", json);
    }

    [Fact] // Spec §4.6 I4: zwei Arrays mit gleichen Elementen in anderer Reihenfolge ⇒ verschieden.
    public void Serialize_DifferentArrayOrder_ProducesDifferentString()
    {
        var a = CanonicalJson.Serialize(new { values = new[] { 3, 1, 2 } });
        var b = CanonicalJson.Serialize(new { values = new[] { 1, 2, 3 } });

        Assert.NotEqual(a, b);
    }

    // ---- 4. Gleicher Inhalt ⇒ gleicher Cache-Prefix-Hash ----

    [Fact] // Spec §6 I4: gleiche Inhalte, andere Key-Reihenfolge ⇒ identischer Hash.
    public void ComputeCachePrefixHash_SameContentDifferentKeyOrder_ProducesIdenticalHash()
    {
        var a = CanonicalJson.ComputeCachePrefixHash(new { b = 1, a = 2 });
        var b = CanonicalJson.ComputeCachePrefixHash(new { a = 2, b = 1 });

        Assert.Equal(a, b);
    }

    [Fact] // Spec §6 I4: unterschiedlicher Inhalt ⇒ unterschiedlicher Hash.
    public void ComputeCachePrefixHash_DifferentContent_ProducesDifferentHash()
    {
        var a = CanonicalJson.ComputeCachePrefixHash(new { a = 1 });
        var b = CanonicalJson.ComputeCachePrefixHash(new { a = 2 });

        Assert.NotEqual(a, b);
    }

    // ---- 5. ContentHash-Stabilität: lowercase Hex-SHA-256 (64 Zeichen) ----

    [Fact] // Spec §4.2 I4: gleicher String ⇒ gleicher Hash.
    public void ContentHash_SameString_ProducesIdenticalHash()
    {
        Assert.Equal(CanonicalJson.ContentHash("hello"), CanonicalJson.ContentHash("hello"));
    }

    [Fact] // Spec §4.2 I4: verschiedener String ⇒ verschiedener Hash.
    public void ContentHash_DifferentString_ProducesDifferentHash()
    {
        Assert.NotEqual(CanonicalJson.ContentHash("hello"), CanonicalJson.ContentHash("world"));
    }

    [Fact] // Spec §4.2 I4: 64-stelliges, lowercase Hex (SHA-256).
    public void ContentHash_IsLowercaseHexSha256()
    {
        var hash = CanonicalJson.ContentHash("hello");

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
            $"Unexpected character '{c}' — expected lowercase hex."));
    }

    [Fact] // Spec §4.2 I4: bekannter SHA-256-Vektor für "abc" (kanonischer Testvektor).
    public void ContentHash_KnownVector_Abc()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            CanonicalJson.ContentHash("abc"));
    }

    // ---- 6. Keine Indentierung/Whitespace (kompakte Form) ----

    [Fact] // Spec §4.6 I4: kompakter Output — kein Newline, kein Indent-Whitespace.
    public void Serialize_ContainsNoIndentationOrNewlines()
    {
        var json = CanonicalJson.Serialize(new
        {
            outer = new { d = 1, c = 2 },
            list = new[] { 1, 2, 3 },
            a = "x",
        });

        Assert.DoesNotContain("\n", json);
        Assert.DoesNotContain("\r", json);
        Assert.DoesNotContain("\t", json);
        // Kein Whitespace zwischen Tokens in der kompakten Form.
        Assert.DoesNotContain(": ", json);
        Assert.DoesNotContain(", ", json);
    }
}
