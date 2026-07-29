using System.Text.Json.Nodes;

namespace JeekTokenPlanUsage;

/// Tool schemas for the debug MCP surface: the McpHost standard tools over the
/// object graph plus log reading. Every tool registered on the host must also
/// appear here, or clients cannot see it.
internal static class DebugMcpContract
{
    public static JsonArray BuildToolList() =>
    [
        Tool(
            "describe",
            "Describe this debug server: object-graph roots, pipe name, and version.",
            new JsonObject()),
        Tool(
            "get_value",
            "Read a value from the live object graph. Paths start at a root, e.g. Context._settings.PollMinutes.",
            new JsonObject
            {
                ["path"] = PathProp(),
                ["depth"] = Prop("integer", "How deep to expand objects (0-5, default 1)."),
            },
            required: ["path"]),
        Tool(
            "set_value",
            "Write a property, field, or list element on the live object graph.",
            new JsonObject
            {
                ["path"] = PathProp(),
                ["value"] = new JsonObject
                {
                    ["description"] = "New value as JSON; {\"$path\": \"...\"} passes a live object reference.",
                },
            },
            required: ["path"]),
        Tool(
            "invoke",
            "Call a method or ICommand on the live object graph. Tasks are awaited up to 60 seconds.",
            new JsonObject
            {
                ["path"] = PathProp(),
                ["args"] = Prop("array", "Positional arguments as JSON values."),
                ["depth"] = Prop("integer", "How deep to expand the result (0-5, default 1)."),
            },
            required: ["path"]),
        Tool(
            "list_members",
            "List the public properties, fields, and methods of the object at a path.",
            new JsonObject
            {
                ["path"] = PathProp(),
            },
            required: ["path"]),
        Tool(
            "read_logs",
            "Read the tail of the app's current log file.",
            new JsonObject
            {
                ["lines"] = Prop("integer", "How many lines to return (1-2000, default 200)."),
                ["filter"] = Prop("string", "Case-insensitive substring filter."),
            }),
    ];

    private static JsonObject PathProp() =>
        Prop("string", "Object path starting at a root: Context. Segments: .Member, [0], [\"key\"], #Name.");

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
