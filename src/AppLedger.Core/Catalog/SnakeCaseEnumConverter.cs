using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppLedger.Core.Catalog;

/// <summary>
/// Reads and writes enum members as the lower_snake_case tokens the catalog uses (`service_group`,
/// `dll_arg_or_system`, `etld1`). Integer values are refused: a rules file must name what it means, so a
/// stray number cannot silently select a rule kind.
/// </summary>
/// <typeparam name="TEnum">The enum being converted.</typeparam>
public sealed class SnakeCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <summary>Creates the converter with the catalog's naming policy.</summary>
    public SnakeCaseEnumConverter()
        : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    {
    }
}
