using System.Globalization;
using System.Resources;

namespace JeekTokenPlanUsage.Resources;

/// Strongly-typed accessor over Strings.resx / Strings.{culture}.resx.
/// Lookups honor CultureInfo.CurrentUICulture, which is set in Program.Main
/// from AppSettings.Language (or left as the OS UI culture when unset).
internal static class Strings
{
    private static readonly ResourceManager Rm =
        new("JeekTokenPlanUsage.Resources.Strings", typeof(Strings).Assembly);

    private static string Get(string key) =>
        Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Menu_RefreshNow => Get(nameof(Menu_RefreshNow));
    public static string Menu_RunAtStartup => Get(nameof(Menu_RunAtStartup));
    public static string Menu_ShowClaude => Get(nameof(Menu_ShowClaude));
    public static string Menu_ShowCodex => Get(nameof(Menu_ShowCodex));
    public static string Menu_ShowCursor => Get(nameof(Menu_ShowCursor));
    public static string Menu_IconDisplay => Get(nameof(Menu_IconDisplay));
    public static string Menu_IconDisplayNone => Get(nameof(Menu_IconDisplayNone));
    public static string Menu_IconDisplaySingle => Get(nameof(Menu_IconDisplaySingle));
    public static string Menu_IconDisplayDouble => Get(nameof(Menu_IconDisplayDouble));
    public static string Menu_RefreshInterval => Get(nameof(Menu_RefreshInterval));
    public static string Menu_Language => Get(nameof(Menu_Language));
    public static string Menu_LanguageAuto => Get(nameof(Menu_LanguageAuto));
    public static string Menu_EnableNotifications => Get(nameof(Menu_EnableNotifications));
    public static string Menu_ShowTaskbarWidget => Get(nameof(Menu_ShowTaskbarWidget));
    public static string Menu_OpenLog => Get(nameof(Menu_OpenLog));
    public static string Menu_CheckForUpdates => Get(nameof(Menu_CheckForUpdates));
    public static string Menu_AutoUpdate => Get(nameof(Menu_AutoUpdate));
    public static string Menu_About => Get(nameof(Menu_About));
    public static string Menu_Exit => Get(nameof(Menu_Exit));

    public static string About_VersionFormat => Get(nameof(About_VersionFormat));
    public static string About_DevBuild => Get(nameof(About_DevBuild));
    public static string About_ProjectLink => Get(nameof(About_ProjectLink));

    public static string Update_FoundTitle => Get(nameof(Update_FoundTitle));
    public static string Update_FoundBodyFormat => Get(nameof(Update_FoundBodyFormat));
    public static string Update_NoneTitle => Get(nameof(Update_NoneTitle));
    public static string Update_NoneBody => Get(nameof(Update_NoneBody));
    public static string Update_FailedFormat => Get(nameof(Update_FailedFormat));

    public static string Notify_Title => Get(nameof(Notify_Title));
    public static string Notify_BodyFormat => Get(nameof(Notify_BodyFormat));

    public static string Tray_Loading => Get(nameof(Tray_Loading));
    public static string Tray_NoData => Get(nameof(Tray_NoData));
    public static string Tray_ResetFormat => Get(nameof(Tray_ResetFormat));
    public static string Tray_WeeklyLabel => Get(nameof(Tray_WeeklyLabel));

    public static string Interval_MinutesFormat => Get(nameof(Interval_MinutesFormat));
    public static string Interval_HoursFormat => Get(nameof(Interval_HoursFormat));

    public static string Error_FetchUsage => Get(nameof(Error_FetchUsage));
    public static string Error_NetworkFormat => Get(nameof(Error_NetworkFormat));
    public static string Error_ParseFormat => Get(nameof(Error_ParseFormat));
    public static string Error_RateLimit429 => Get(nameof(Error_RateLimit429));

    public static string Claude_ReadCredFailed => Get(nameof(Claude_ReadCredFailed));
    public static string Claude_TokenInvalid => Get(nameof(Claude_TokenInvalid));
    public static string Claude_AuthRequiredTitle => Get(nameof(Claude_AuthRequiredTitle));
    public static string Claude_AuthRequiredBody => Get(nameof(Claude_AuthRequiredBody));
    public static string Claude_CredNotFound => Get(nameof(Claude_CredNotFound));
    public static string Claude_CredMissingToken => Get(nameof(Claude_CredMissingToken));
    public static string Claude_ReadCredFailedFormat => Get(nameof(Claude_ReadCredFailedFormat));
    public static string Claude_MessagesNoHeaderFormat => Get(nameof(Claude_MessagesNoHeaderFormat));
    public static string Claude_MessagesFallbackFailed => Get(nameof(Claude_MessagesFallbackFailed));

    public static string Codex_CurlNotFound => Get(nameof(Codex_CurlNotFound));
    public static string Codex_ReadCredFailed => Get(nameof(Codex_ReadCredFailed));
    public static string Codex_CurlFailedFormat => Get(nameof(Codex_CurlFailedFormat));
    public static string Codex_TokenInvalid => Get(nameof(Codex_TokenInvalid));
    public static string Codex_AuthRequiredTitle => Get(nameof(Codex_AuthRequiredTitle));
    public static string Codex_AuthRequiredBody => Get(nameof(Codex_AuthRequiredBody));
    public static string Codex_ResponseMissingRateLimit => Get(nameof(Codex_ResponseMissingRateLimit));
    public static string Codex_AuthNotFound => Get(nameof(Codex_AuthNotFound));
    public static string Codex_AuthMissingToken => Get(nameof(Codex_AuthMissingToken));

    public static string Cursor_ReadCredFailed => Get(nameof(Cursor_ReadCredFailed));
    public static string Cursor_TokenInvalid => Get(nameof(Cursor_TokenInvalid));
    public static string Cursor_ResponseMissingPlan => Get(nameof(Cursor_ResponseMissingPlan));
    public static string Cursor_DbNotFound => Get(nameof(Cursor_DbNotFound));
    public static string Cursor_CredEmpty => Get(nameof(Cursor_CredEmpty));
    public static string Cursor_ReadCredFailedFormat => Get(nameof(Cursor_ReadCredFailedFormat));

    public static string Jwt_Invalid => Get(nameof(Jwt_Invalid));
    public static string Jwt_MissingSub => Get(nameof(Jwt_MissingSub));
    public static string Jwt_EmptySub => Get(nameof(Jwt_EmptySub));
    public static string Jwt_ParseFailedFormat => Get(nameof(Jwt_ParseFailedFormat));
}
