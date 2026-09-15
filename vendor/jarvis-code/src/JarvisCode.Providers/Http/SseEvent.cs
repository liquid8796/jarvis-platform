namespace JarvisCode.Providers.Http;

/// <summary>One server-sent event: optional event name plus the joined data payload.</summary>
public sealed record SseEvent(string? EventName, string Data);
