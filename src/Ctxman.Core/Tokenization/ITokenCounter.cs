namespace Ctxman.Core.Tokenization;

/// <summary>
/// Pluggable Token-Zähler (Spec §8). Bildet einen Content-String auf die geschätzte Anzahl
/// Tokens ab. Exakte Zählung ist nicht kritisch — Watermarks sind Verhältniswerte — daher
/// genügt eine konservative Approximation; provider-genaue Tokenizer sind eigene Implementierungen.
/// Reines Core-Interface ohne I/O.
/// </summary>
public interface ITokenCounter
{
    /// <summary>Schätzt die Token-Anzahl des übergebenen Inhalts.</summary>
    int Count(string content);
}
