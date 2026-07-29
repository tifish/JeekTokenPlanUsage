using System.Text.Json.Nodes;

namespace JeekTokenPlanUsage;

/// Tool schemas for the product MCP surface. Every tool registered on the host
/// must also appear here, or clients cannot see it. The standard object-graph
/// tools (get_value / set_value / invoke / ...) are deliberately absent: the
/// product surface exposes app features only, never process internals.
internal static class ProductMcpContract
{
    public static JsonArray BuildToolList() =>
    [
        Tool(
            "get_usage",
            "Return current Claude, Codex, Cursor, and Grok plan usage from the tray app.",
            new JsonObject
            {
                ["provider"] = ProviderProp("Optional provider id. Omit it to return all providers."),
                ["refresh"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "When true, refresh usage before returning the snapshot.",
                },
            }),
        Tool(
            "refresh_usage",
            "Refresh one provider or all providers, then return the updated usage snapshot.",
            new JsonObject
            {
                ["provider"] = ProviderProp("Optional provider id. Omit it to refresh all providers."),
            }),
        Tool(
            "get_ui_state",
            "Return the tray app's current UI and settings state for automation.",
            new JsonObject()),
        Tool(
            "ui_action",
            "Invoke a tray-menu-equivalent UI action for automation and testing.",
            new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(
                        "refresh", "set_paused", "set_provider_enabled", "set_icon_display",
                        "set_poll_interval", "set_language", "set_threshold_notifications",
                        "set_taskbar_widget", "set_startup", "set_auto_update", "set_proxy",
                        "set_storage", "show_details", "hide_details", "toggle_details",
                        "open_log", "check_update", "show_about", "exit_app"),
                },
                ["provider"] = ProviderProp("Provider id for provider-specific actions."),
                ["paused"] = Prop("boolean", "Required by set_paused."),
                ["enabled"] = Prop(
                    "boolean",
                    "Required by set_provider_enabled, set_threshold_notifications, set_startup, and set_auto_update."),
                ["visible"] = Prop("boolean", "Optional for set_taskbar_widget."),
                ["mode"] = Prop("string", "Icon mode, proxy mode, or storage mode depending on the action."),
                ["minutes"] = Prop("integer", "Required by set_poll_interval."),
                ["language"] = Prop(
                    "string", "Required by set_language. Use empty string to follow system UI language."),
                ["offset"] = Prop("integer", "Optional taskbar widget offset for set_taskbar_widget."),
                ["protocol"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("socks5", "http"),
                    ["description"] = "Optional custom proxy protocol for set_proxy.",
                },
                ["host"] = Prop("string", "Optional custom proxy host for set_proxy."),
                ["port"] = Prop("integer", "Optional custom proxy port for set_proxy."),
                ["customRoot"] = Prop(
                    "string",
                    "Required when set_storage mode is custom and no custom root is already saved."),
                ["allowUpdateLaunch"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "When true, check_update may launch the updater if one is available.",
                },
            },
            required: ["action"]),
    ];

    private static JsonObject ProviderProp(string description) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray("claude", "codex", "cursor", "grok"),
        ["description"] = description,
    };

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static JsonObject Tool(
        string name, string description, JsonObject properties, string[]? required = null)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required is { Length: > 0 })
            schema["required"] = new JsonArray(required.Select(JsonNode (r) => r).ToArray());
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };
    }
}
