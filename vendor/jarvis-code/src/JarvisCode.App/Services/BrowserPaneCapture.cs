using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>
/// Folds Browser pane DevTools protocol events into the per-tab console and
/// network buffers — a C# port of the Jarvis Browser extension's
/// chrome.debugger.onEvent listener (Assets/JarvisBrowser/background.js), so
/// the buffers come out in the exact shapes <see cref="JarvisBrowserFormat"/>
/// renders. Pure JSON-in, list-out; unit tested without a browser.
/// </summary>
public static class BrowserPaneCapture
{
    public const int ConsoleCap = 1000;
    public const int NetworkCap = 500;

    /// <summary>The CDP events a pane tab subscribes to.</summary>
    public static readonly IReadOnlyList<string> Events =
    [
        "Runtime.consoleAPICalled",
        "Runtime.exceptionThrown",
        "Log.entryAdded",
        "Network.requestWillBeSent",
        "Network.responseReceived",
        "Network.loadingFinished",
        "Network.loadingFailed",
        "Page.frameNavigated",
    ];

    /// <summary>
    /// True for the event that retires per-page state: a navigation committed by the main
    /// frame, which is what clears the buffers below and the tab's pending notices.
    /// </summary>
    public static bool IsMainFrameNavigation(string eventName, JsonObject parameters) =>
        eventName == "Page.frameNavigated"
        && parameters["frame"] is JsonObject frame
        && frame["parentId"] is null;

    public static void Apply(
        string eventName, JsonObject parameters,
        List<JsonObject> console, Dictionary<string, JsonObject> network, List<string> netOrder)
    {
        switch (eventName)
        {
            case "Runtime.consoleAPICalled":
            {
                var type = parameters["type"]?.GetValue<string>() ?? "log";
                var text = string.Join(" ",
                    (parameters["args"] as JsonArray ?? []).OfType<JsonObject>().Select(RemoteToText));
                PushConsole(console, new JsonObject
                {
                    ["level"] = type == "warning" ? "warn" : type,
                    ["text"] = text,
                    ["ts"] = parameters["timestamp"]?.DeepClone(),
                });
                break;
            }

            case "Runtime.exceptionThrown":
            {
                var details = parameters["exceptionDetails"] as JsonObject ?? [];
                var exception = details["exception"] as JsonObject;
                var description = exception?["description"]?.GetValue<string>()
                    ?? exception?["value"]?.ToString()
                    ?? details["text"]?.GetValue<string>()
                    ?? "Uncaught exception";
                PushConsole(console, new JsonObject
                {
                    ["level"] = "error",
                    ["text"] = description,
                    ["url"] = details["url"]?.DeepClone(),
                    ["ts"] = parameters["timestamp"]?.DeepClone(),
                });
                break;
            }

            case "Log.entryAdded":
            {
                var entry = parameters["entry"] as JsonObject ?? [];
                var level = entry["level"]?.GetValue<string>() ?? "log";
                PushConsole(console, new JsonObject
                {
                    ["level"] = level == "warning" ? "warn" : level,
                    ["text"] = entry["text"]?.GetValue<string>() ?? "",
                    ["url"] = entry["url"]?.DeepClone(),
                    ["source"] = entry["source"]?.DeepClone(),
                    ["ts"] = entry["timestamp"]?.DeepClone(),
                });
                break;
            }

            case "Network.requestWillBeSent":
            {
                if (parameters["requestId"]?.GetValue<string>() is not { } id)
                {
                    break;
                }

                var request = parameters["request"] as JsonObject ?? [];
                if (!network.ContainsKey(id))
                {
                    netOrder.Add(id);
                    if (netOrder.Count > NetworkCap)
                    {
                        network.Remove(netOrder[0]);
                        netOrder.RemoveAt(0);
                    }
                }

                network[id] = new JsonObject
                {
                    ["requestId"] = id,
                    ["url"] = request["url"]?.DeepClone(),
                    ["method"] = request["method"]?.DeepClone(),
                    ["type"] = parameters["type"]?.DeepClone(),
                    ["status"] = 0,
                    ["finished"] = false,
                    ["ts"] = parameters["timestamp"]?.DeepClone(),
                };
                break;
            }

            case "Network.responseReceived":
            {
                if (Entry(parameters) is { } entry)
                {
                    var response = parameters["response"] as JsonObject ?? [];
                    entry["status"] = response["status"]?.DeepClone();
                    entry["statusText"] = response["statusText"]?.DeepClone();
                    entry["mimeType"] = response["mimeType"]?.DeepClone();
                }

                break;
            }

            case "Network.loadingFinished":
            {
                if (Entry(parameters) is { } entry)
                {
                    entry["finished"] = true;
                    entry["size"] = parameters["encodedDataLength"]?.DeepClone();
                }

                break;
            }

            case "Network.loadingFailed":
            {
                if (Entry(parameters) is { } entry)
                {
                    entry["finished"] = true;
                    entry["failed"] = true;
                    entry["errorText"] = parameters["errorText"]?.DeepClone();
                }

                break;
            }

            case "Page.frameNavigated":
            {
                // Main-frame navigation: fresh page, fresh logs (DevTools default).
                if (IsMainFrameNavigation(eventName, parameters))
                {
                    console.Clear();
                    network.Clear();
                    netOrder.Clear();
                }

                break;
            }
        }

        JsonObject? Entry(JsonObject p) =>
            p["requestId"]?.GetValue<string>() is { } id && network.TryGetValue(id, out var entry) ? entry : null;
    }

    private static void PushConsole(List<JsonObject> console, JsonObject entry)
    {
        console.Add(entry);
        if (console.Count > ConsoleCap)
        {
            console.RemoveAt(0);
        }
    }

    /// <summary>Renders one CDP RemoteObject as display text — the extension's remoteToText.</summary>
    public static string RemoteToText(JsonObject? remote)
    {
        if (remote is null)
        {
            return "";
        }

        if (remote["type"]?.GetValue<string>() == "string")
        {
            return remote["value"]?.GetValue<string>() ?? "";
        }

        if (remote["unserializableValue"]?.GetValue<string>() is { } unserializable)
        {
            return unserializable;
        }

        if (remote.ContainsKey("value"))
        {
            var value = remote["value"];
            return value is JsonObject or JsonArray ? value.ToJsonString() : value?.ToString() ?? "null";
        }

        if (remote["preview"] is JsonObject preview && preview["properties"] is JsonArray properties)
        {
            var inner = string.Join(", ", properties.OfType<JsonObject>()
                .Select(static p => $"{p["name"]?.GetValue<string>()}: {p["value"]?.GetValue<string>()}"));
            return $"{preview["description"]?.GetValue<string>() ?? ""}{{{inner}}}";
        }

        return remote["description"]?.GetValue<string>() ?? remote["type"]?.GetValue<string>() ?? "";
    }
}
