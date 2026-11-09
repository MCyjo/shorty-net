namespace Shorty.Api;

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Applied database migration {Version}")]
    public static partial void MigrationApplied(this ILogger logger, string version);
}
