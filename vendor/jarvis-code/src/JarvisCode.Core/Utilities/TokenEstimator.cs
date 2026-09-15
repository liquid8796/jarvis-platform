using JarvisCode.Core.Models;

namespace JarvisCode.Core.Utilities;

/// <summary>
/// Rough token estimation (~4 characters per token) used for the context meter
/// and history trimming. Providers report exact usage after each response.
/// </summary>
public static class TokenEstimator
{
    private const int CharsPerToken = 4;

    public static int Estimate(string text) => (text.Length + CharsPerToken - 1) / CharsPerToken;

    public static int Estimate(ChatMessage message)
    {
        int total = 0;
        foreach (var block in message.Content)
        {
            total += block switch
            {
                TextBlock text => Estimate(text.Text),
                ToolCallBlock call => Estimate(call.Name) + Estimate(call.ArgumentsJson) + 8,
                ToolResultBlock result => Estimate(result.Content) + 8,
                ThinkingBlock thinking => Estimate(thinking.Thinking) + 8,
                RawProviderBlock raw => Estimate(raw.RawJson),
                ImageBlock => 1100, // rough cost of one downscaled screenshot

                _ => 0,
            };
        }
        return total + 4;
    }

    public static int Estimate(IEnumerable<ChatMessage> messages) => messages.Sum(Estimate);
}
