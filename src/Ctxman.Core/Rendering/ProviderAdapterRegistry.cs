namespace Ctxman.Core.Rendering;

/// <summary>
/// DI-Registry für zustandslose <see cref="IProviderAdapter"/>-Implementierungen (Spec §4.6, §8).
/// Löst den offenen <c>provider</c>-String aus <c>render</c> auf; unbekannter Provider ⇒ 400
/// mit Liste der registrierten Adapter.
/// </summary>
public sealed class ProviderAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, IProviderAdapter> _adapters;

    public ProviderAdapterRegistry(IEnumerable<IProviderAdapter> adapters)
    {
        _adapters = adapters.ToDictionary(a => a.Provider, StringComparer.Ordinal);
    }

    public bool TryGet(string provider, out IProviderAdapter? adapter) =>
        _adapters.TryGetValue(provider, out adapter);

    public IReadOnlyList<string> RegisteredProviders =>
        _adapters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
}
