using System.Text;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.App.Services;

/// <summary>
/// The question the loader asks before it follows an <c>@</c>-import that
/// reaches outside the working directory, ported from the reference's
/// ClaudeMdExternalIncludesDialog (CLI 2.1.251, its <c>iRt</c>): the same title,
/// the same warning, the same list capped the same way, the same closing note,
/// and its two buttons in its own order — refusing first, because the safe
/// answer is the one a stray Enter should give.
/// </summary>
internal static class InstructionPrompts
{
    /// <summary>How many imports the reference lists before it counts the rest (its <c>x8</c>).</summary>
    public const int ListedExternalImports = 8;

    public const string ExternalImportsHeader = "External imports";

    public const string ExternalImportsTitle = "Allow external JARVIS.md file imports?";

    public const string ExternalImportsWarning =
        "This project's JARVIS.md imports files outside the current working directory. " +
        "Never allow this for third-party repositories.";

    public const string ExternalImportsListHeader = "External imports:";

    public const string ExternalImportsCoverage =
        "Yes covers those too, plus any this project adds later.";

    public const string ExternalImportsTrust =
        "Important: Only use Jarvis Code with files you trust. " +
        "Accessing untrusted files may pose security risks";

    public const string ExternalImportsAllow = "Yes, allow external imports";

    public const string ExternalImportsRefuse = "No, disable external imports";

    /// <summary>The card the reference raises once per project, or null when there is nothing to ask about.</summary>
    public static UserQuestion? ExternalImports(IReadOnlyList<ProjectInstructions.ExternalImport> imports)
    {
        if (imports.Count == 0)
        {
            return null;
        }

        var body = new StringBuilder(ExternalImportsTitle)
            .Append("\n\n")
            .Append(ExternalImportsWarning)
            .Append("\n\n")
            .Append(ExternalImportsListHeader)
            .Append('\n');

        var listed = imports.Count <= ListedExternalImports
            ? imports
            : imports.Take(ListedExternalImports - 2).ToList();
        foreach (var import in listed)
        {
            body.Append("  ").Append(import.FilePath).Append('\n');
        }

        int hidden = imports.Count - listed.Count;
        if (hidden > 0)
        {
            body.Append("  … +").Append(hidden).Append(' ')
                .Append(hidden == 1 ? "import" : "imports").Append(" not shown.\n")
                .Append("  ").Append(ExternalImportsCoverage).Append('\n');
        }

        body.Append('\n').Append(ExternalImportsTrust);

        return new UserQuestion(
            body.ToString(),
            ExternalImportsHeader,
            [
                new UserQuestionOption(ExternalImportsRefuse, "Imports outside this folder are not loaded."),
                new UserQuestionOption(ExternalImportsAllow, "This project may import files from anywhere."),
            ],
            MultiSelect: false);
    }

    /// <summary>True when the answer given to <see cref="ExternalImports"/> allowed them.</summary>
    public static bool Approved(UserQuestionAnswers? answers) =>
        answers is not null &&
        answers.Answers.Values.Any(value =>
            value.Contains(ExternalImportsAllow, StringComparison.Ordinal));
}
