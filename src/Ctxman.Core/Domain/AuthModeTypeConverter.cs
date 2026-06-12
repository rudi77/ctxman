using System.ComponentModel;
using System.Globalization;

namespace Ctxman.Core.Domain;

/// <summary>
/// Erlaubt dem .NET-Konfigurationsbinder, snake_case-Werte wie <c>api_key</c> auf
/// <see cref="AuthMode"/> zu binden — ohne diesen Converter würde der Binder
/// <c>FormatException: api_key is not a valid value for AuthMode</c> werfen,
/// weil der Enum-Member <c>ApiKey</c> (PascalCase) heißt. Spec §4.1.
/// </summary>
internal sealed class AuthModeTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string s)
        {
            // Spec §4.1: Erst snake_case-Wire-Werte (none|api_key|jwt) probieren,
            // dann Enum-Member-Namen (None|ApiKey|Jwt) als Fallback —
            // damit bestehende Tests, die "ApiKey"/"Jwt" als String binden, weiter grün bleiben.
            try
            {
                return EnumWire.ParseAuthMode(s);
            }
            catch (ArgumentException firstEx)
            {
                try
                {
                    return Enum.Parse<AuthMode>(s, ignoreCase: true);
                }
                catch (ArgumentException)
                {
                    // Beide Wege scheitern: den ursprünglichen Fehler werfen.
                    throw firstEx;
                }
            }
        }

        return base.ConvertFrom(context, culture, value);
    }
}
