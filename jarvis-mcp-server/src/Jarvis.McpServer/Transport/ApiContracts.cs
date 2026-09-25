using System.ComponentModel.DataAnnotations;
using Jarvis.McpServer.Domain;
namespace Jarvis.McpServer.Transport;
public sealed record RegisterRequest([Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(100, MinimumLength = 1)] string DisplayName, [Required, StringLength(256, MinimumLength = 14)] string Password);
public sealed record LoginRequest([Required, EmailAddress, StringLength(254)] string Email, [Required, StringLength(256)] string Password);
public sealed record DeviceRequest([Required, StringLength(100, MinimumLength = 1)] string Name, bool Enabled = true, string? Revision = null);
public sealed record ToolRequest([Required, StringLength(64)] string Name, [Required, StringLength(100)] string AgentToolId,
    [Required, StringLength(8000)] string Description, ToolPublicationMode? PublicationMode = null,
    bool? Enabled = null, string? Revision = null)
{
    public ToolPublicationMode RequestedPublicationMode => PublicationMode ?? (Enabled switch
    {
        true => ToolPublicationMode.Published,
        false => ToolPublicationMode.Hidden,
        _ => ToolPublicationMode.Auto
    });
}
public sealed record AdminUserRequest([Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(100)] string DisplayName, [Required, RegularExpression("^(user|admin)$")] string Role,
    [Required, RegularExpression("^(pending|active|disabled)$")] string Status, [StringLength(256, MinimumLength = 14)] string? Password = null);
