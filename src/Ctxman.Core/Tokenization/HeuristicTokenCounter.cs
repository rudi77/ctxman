namespace Ctxman.Core.Tokenization;

/// <summary>
/// Konservative Default-Heuristik (Spec §8): ~4 Zeichen pro Token, aufgerundet. Liefert für
/// nicht-leeren Inhalt stets ≥ 1, für leeren/null-Inhalt 0. Kein I/O, kein externer Tokenizer —
/// als sicherer Fallback gedacht, wenn kein provider-genauer Zähler registriert ist.
/// </summary>
public sealed class HeuristicTokenCounter : ITokenCounter
{
    private const double CharsPerToken = 4.0;

    public int Count(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        return (int)Math.Ceiling(content.Length / CharsPerToken);
    }
}
