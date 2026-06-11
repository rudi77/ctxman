namespace Ctxman.Core.Domain;

/// <summary>
/// Leitet den Watermark-Status aus den verbrauchten Tokens relativ zum Budget ab (Spec §3.1):
/// höchste überschrittene Schwelle gewinnt; keine überschritten ⇒ "ok". Reine Berechnung —
/// kein GC wird ausgelöst. Gemeinsame Quelle für Render-Pipeline und Session-Endpunkte, damit
/// der Status an einer Stelle definiert ist.
/// </summary>
public static class WatermarkState
{
    /// <summary>Watermark-Status (ok|soft|hard|emergency) als Wire-String (Spec §3.1).</summary>
    public static string Derive(int tokensUsed, PolicyConfig policy)
    {
        var ratio = policy.BudgetTokens <= 0 ? 0.0 : (double)tokensUsed / policy.BudgetTokens;
        var w = policy.Watermarks;

        if (ratio >= w.Emergency)
        {
            return "emergency";
        }

        if (ratio >= w.Hard)
        {
            return "hard";
        }

        if (ratio >= w.Soft)
        {
            return "soft";
        }

        return "ok";
    }
}
