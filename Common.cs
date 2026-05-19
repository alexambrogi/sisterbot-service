namespace SisterBotService
{
    internal static partial class Common
    {
        internal static IConfiguration Configuration { get; set; } = null!;
        internal static int DefaultCommandTimeout { get; set; }
        internal static ConnectionData ConnectionData { get; set; } = null!;
        internal static int TimeZoneAdd { get; set; }
        internal static string TenantId { get; set; } = string.Empty;
        internal static string ClientId { get; set; } = string.Empty;
        internal static string ClientSecret { get; set; } = string.Empty;
        internal static string FromEmail { get; set; } = string.Empty;
        internal static NotifierEmailData NotifierEmailData { get; set; } = null!;
    }
}
