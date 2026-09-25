using System.ComponentModel.DataAnnotations;
using Jarvis.McpServer.Domain;
namespace Jarvis.McpServer.Transport;

public sealed record ToolRevisionRequest(
    [Required, StringLength(100)] string Id,
    [Required, StringLength(100)] string Revision);

public sealed record BulkToolAvailabilityRequest(
    [Required, MinLength(1), MaxLength(500)] ToolRevisionRequest[] Tools,
    ToolPublicationMode? PublicationMode = null,
    bool? Enabled = null) : IValidatableObject
{
    public ToolPublicationMode RequestedPublicationMode => PublicationMode ?? (Enabled switch
    {
        true => ToolPublicationMode.Published,
        false => ToolPublicationMode.Hidden,
        _ => ToolPublicationMode.Auto
    });

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Tools is null) yield break; // [Required] handles missing input.
        if (PublicationMode is null && Enabled is null)
            yield return new ValidationResult("publicationMode is required.", [nameof(PublicationMode)]);
        else if (PublicationMode is not null && Enabled is not null)
            yield return new ValidationResult("Specify publicationMode or legacy enabled, not both.", [nameof(PublicationMode), nameof(Enabled)]);
        if (Tools.Any(t => t is null))
            yield return new ValidationResult("Tool selections cannot contain null items.", [nameof(Tools)]);
        else if (Tools.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != Tools.Length)
            yield return new ValidationResult("Select each tool only once.", [nameof(Tools)]);
    }
}
