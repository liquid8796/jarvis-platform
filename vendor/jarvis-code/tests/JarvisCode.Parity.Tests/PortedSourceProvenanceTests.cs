using System.Text.RegularExpressions;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Provenance on the ported sources: which reference build each file's text was
/// read from.
/// </summary>
/// <remarks>
/// A delta row records <em>why</em> a line differs from the reference. Neither
/// it nor the source list recorded <em>when</em> it was checked, so after a
/// reference release there was no way to tell which files had been measured
/// against which build — every mismatch looked alike. The field is optional,
/// because inventing a provenance for a file nobody measured would be worse
/// than leaving it blank; what is enforced is that a stamp, once given, is a
/// real build and shows up where it helps.
/// </remarks>
public class PortedSourceProvenanceTests
{
    private static readonly Regex Build = new(@"\d+\.\d+", RegexOptions.Compiled);

    [Fact]
    public void A_recorded_provenance_names_a_build()
    {
        foreach (var source in PortedTextParityTests.Sources)
        {
            if (source.MeasuredAgainst is not { } measured)
            {
                continue;
            }

            Assert.True(measured.Trim().Length > 0,
                $"{source.Path} declares an empty provenance; leave it null instead");
            Assert.True(Build.IsMatch(measured),
                $"{source.Path} declares '{measured}', which names no build version");
        }
    }

    [Fact]
    public void The_sources_measured_this_round_carry_it()
    {
        // These two were read out of the installed builds rather than inherited
        // from an earlier round, so they are the ones that can honestly say
        // which build they came from.
        string[] stamped =
        [
            "JarvisCode.App/Services/HostPromptSections.cs",
            "JarvisCode.App/Services/OutputStyles.cs",
        ];

        foreach (var path in stamped)
        {
            var source = PortedTextParityTests.Sources.Single(s => s.Path == path);
            Assert.NotNull(source.MeasuredAgainst);
        }
    }

    [Fact]
    public void Provenance_matches_the_side_the_source_is_checked_against()
    {
        foreach (var source in PortedTextParityTests.Sources)
        {
            if (source.MeasuredAgainst is not { } measured)
            {
                continue;
            }

            var expected = source.Side == ReferenceSide.Cli ? "CLI" : "desktop";
            Assert.Contains(expected, measured, StringComparison.OrdinalIgnoreCase);
        }
    }
}
