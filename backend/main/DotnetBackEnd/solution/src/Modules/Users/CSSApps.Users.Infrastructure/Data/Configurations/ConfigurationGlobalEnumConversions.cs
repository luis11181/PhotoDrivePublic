using Microsoft.EntityFrameworkCore;

namespace CSSApps.Users.Infrastructure.Data.Configurations;

public static class ConfigurationGlobalEnumConversions
{
    public static void ConfigGlobalEnumConversions(this ModelConfigurationBuilder configurationBuilder)
    {
        // configurationBuilder.
        //     Properties<TypeOfMessageContent>()
        //     .HaveConversion<MessageTypeConverter>();
    }
}

//MessageType
//
// public class MessageTypeConverter : ValueConverter<TypeOfMessageContent?, string?>
// {
//     public MessageTypeConverter()
//         : base(
//                v => v.ToString(),        // Properly handle null by providing a default string
//                v => ConvertFromString(v)
//     )
//     {
//     }
//
//     private static TypeOfMessageContent ConvertFromString(string? v)
//     {
//         if (string.IsNullOrEmpty(v)) return TypeOfMessageContent.Default;
//
//         return Enum.TryParse<TypeOfMessageContent>(v, true, out var result) ? result : TypeOfMessageContent.Default;
//     }
// }
//
//