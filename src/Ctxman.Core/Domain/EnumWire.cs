namespace Ctxman.Core.Domain;

/// <summary>
/// Reine String-Abbildung der Domänen-Enums auf ihre snake_case-Wire-Repräsentation
/// (Spec §2, Wire-Format wie in CLAUDE.md festgelegt). Wird von EF-ValueConvertern und
/// JSON gleichermaßen genutzt; bewusst ohne ASP.NET- oder EF-Abhängigkeit.
/// </summary>
public static class EnumWire
{
    public static string ToWire(this Region value) => value switch
    {
        Region.Static => "static",
        Region.Working => "working",
        _ => throw UnknownValue(value),
    };

    public static string ToWire(this Role value) => value switch
    {
        Role.System => "system",
        Role.User => "user",
        Role.Assistant => "assistant",
        Role.Tool => "tool",
        _ => throw UnknownValue(value),
    };

    public static string ToWire(this SegmentState value) => value switch
    {
        SegmentState.Live => "live",
        SegmentState.Externalized => "externalized",
        SegmentState.Compacted => "compacted",
        SegmentState.Evicted => "evicted",
        _ => throw UnknownValue(value),
    };

    public static string ToWire(this SessionStatus value) => value switch
    {
        SessionStatus.Active => "active",
        SessionStatus.Archived => "archived",
        _ => throw UnknownValue(value),
    };

    public static string ToWire(this FrameStatus value) => value switch
    {
        FrameStatus.Open => "open",
        FrameStatus.Popped => "popped",
        _ => throw UnknownValue(value),
    };

    public static string ToWire(this AuthMode value) => value switch
    {
        AuthMode.None => "none",
        AuthMode.ApiKey => "api_key",
        AuthMode.Jwt => "jwt",
        _ => throw UnknownValue(value),
    };

    public static Region ParseRegion(string wire) => wire switch
    {
        "static" => Region.Static,
        "working" => Region.Working,
        _ => throw UnknownWire<Region>(wire),
    };

    public static Role ParseRole(string wire) => wire switch
    {
        "system" => Role.System,
        "user" => Role.User,
        "assistant" => Role.Assistant,
        "tool" => Role.Tool,
        _ => throw UnknownWire<Role>(wire),
    };

    public static SegmentState ParseSegmentState(string wire) => wire switch
    {
        "live" => SegmentState.Live,
        "externalized" => SegmentState.Externalized,
        "compacted" => SegmentState.Compacted,
        "evicted" => SegmentState.Evicted,
        _ => throw UnknownWire<SegmentState>(wire),
    };

    public static SessionStatus ParseSessionStatus(string wire) => wire switch
    {
        "active" => SessionStatus.Active,
        "archived" => SessionStatus.Archived,
        _ => throw UnknownWire<SessionStatus>(wire),
    };

    public static FrameStatus ParseFrameStatus(string wire) => wire switch
    {
        "open" => FrameStatus.Open,
        "popped" => FrameStatus.Popped,
        _ => throw UnknownWire<FrameStatus>(wire),
    };

    public static AuthMode ParseAuthMode(string wire) => wire switch
    {
        "none" => AuthMode.None,
        "api_key" => AuthMode.ApiKey,
        "jwt" => AuthMode.Jwt,
        _ => throw UnknownWire<AuthMode>(wire),
    };

    private static ArgumentOutOfRangeException UnknownValue<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        new(nameof(value), value, $"Unbekannter {typeof(TEnum).Name}-Wert.");

    private static ArgumentException UnknownWire<TEnum>(string wire) =>
        new($"Unbekannter Wire-Wert '{wire}' für {typeof(TEnum).Name}.", nameof(wire));
}
