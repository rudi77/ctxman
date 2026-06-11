using Ctxman.Core.Tokenization;

namespace Ctxman.Tests.Tokenization;

/// <summary>
/// Tests der konservativen Default-Heuristik (Spec §8): 0 für leeren Inhalt, ≥ 1 für nicht-leeren,
/// monoton nicht-fallend mit der Länge.
/// </summary>
public sealed class HeuristicTokenCounterTests
{
    private readonly ITokenCounter _counter = new HeuristicTokenCounter();

    [Fact]
    public void Count_EmptyString_ReturnsZero()
    {
        Assert.Equal(0, _counter.Count(string.Empty));
    }

    [Fact]
    public void Count_Null_ReturnsZero()
    {
        Assert.Equal(0, _counter.Count(null!));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("abcd")]
    [InlineData("hello world, this is some content")]
    public void Count_NonEmpty_ReturnsPositive(string content)
    {
        Assert.True(_counter.Count(content) > 0);
    }

    [Fact]
    public void Count_IsMonotonicWithLength()
    {
        var shorter = _counter.Count("abcd");
        var longer = _counter.Count("abcdefghijklmnop");

        Assert.True(longer > shorter);
    }

    [Fact] // chars/4 aufgerundet: 5 Zeichen ⇒ ceil(5/4) = 2.
    public void Count_RoundsUp()
    {
        Assert.Equal(2, _counter.Count("abcde"));
    }
}
