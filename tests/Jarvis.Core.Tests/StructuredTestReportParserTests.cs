using Jarvis.Agent.Core.DeveloperTools;

namespace Jarvis.Core.Tests;

public sealed class StructuredTestReportParserTests
{
    private static string Trx(string outcome, int passed, int failed, int skipped = 0) =>
        $"<TestRun><Results><UnitTestResult testName='sample' outcome='{outcome}'/></Results><ResultSummary outcome='Completed'><Counters total='1' executed='{passed + failed}' passed='{passed}' failed='{failed}' notExecuted='{skipped}'/></ResultSummary></TestRun>";
    private static string Js(string status, int passed, int failed, int pending = 0) =>
        $$"""{"numTotalTests":1,"numPassedTests":{{passed}},"numFailedTests":{{failed}},"numPendingTests":{{pending}},"testResults":[{"assertionResults":[{"status":"{{status}}"}]}]}""";

    [Theory]
    [InlineData("trx", "<TestRun><Results/><ResultSummary><Counters total='0' passed='0' failed='0'/></ResultSummary></TestRun>")]
    [InlineData("junit", "<testsuite tests='0' failures='0'/>")]
    [InlineData("jest", "{\"numTotalTests\":0,\"numPassedTests\":0,\"numFailedTests\":0,\"testResults\":[]}")]
    [InlineData("vitest", "{\"numTotalTests\":0,\"numPassedTests\":0,\"numFailedTests\":0,\"testResults\":[]}")]
    [InlineData("go-json", "{\"Action\":\"pass\",\"Package\":\"fixture\"}")]
    public void Recognized_zero_test_reports_never_pass(string format, string report)
    {
        var result = StructuredTestReportParser.Parse(format, report, 0);
        Assert.True(result.ReportParsed);
        Assert.Equal("not_run", result.State);
        Assert.Equal(0, result.Executed);
    }

    [Fact]
    public void Skipped_only_and_missing_reports_never_pass()
    {
        var skipped = StructuredTestReportParser.Parse("trx", Trx("NotExecuted", 0, 0, 1), 0);
        Assert.Equal("not_run", skipped.State); Assert.Equal(1, skipped.Skipped);
        Assert.Equal("not_run", StructuredTestReportParser.Parse("junit", "<testsuite tests='1' skipped='1'><testcase name='skip'><skipped/></testcase></testsuite>", 0).State);
        Assert.False(StructuredTestReportParser.Parse("trx", "Passed! Total: 1", 0).ReportParsed);
        Assert.False(StructuredTestReportParser.Parse("trx", "", 0).ReportParsed);
        Assert.Equal("unrecognized", StructuredTestReportParser.Parse("made-up", "ok", 0).State);
    }

    [Fact]
    public void Exit_failure_and_report_failures_are_authoritative()
    {
        Assert.Equal("failed", StructuredTestReportParser.Parse("trx", Trx("Passed", 1, 0), 9).State);
        Assert.Equal("failed", StructuredTestReportParser.Parse("trx", Trx("Failed", 0, 1), 0).State);
        Assert.Equal("failed", StructuredTestReportParser.Parse("jest", Js("failed", 0, 1), 0).State);
        Assert.Equal("failed", StructuredTestReportParser.Parse("junit", "<testsuite tests='1' errors='1'><testcase name='error'><error>boom</error></testcase></testsuite>", 0).State);
    }

    [Fact]
    public void Inconsistent_counters_and_unknown_outcomes_are_rejected()
    {
        Assert.Equal("invalid_report", StructuredTestReportParser.Parse("trx", Trx("Passed", 0, 1), 0).State);
        Assert.Equal("invalid_report", StructuredTestReportParser.Parse("trx", Trx("Mystery", 1, 0), 0).State);
        Assert.Equal("invalid_report", StructuredTestReportParser.Parse("junit", "<testsuite tests='2'><testcase name='only-one'/></testsuite>", 0).State);
        Assert.Equal("invalid_report", StructuredTestReportParser.Parse("jest", Js("passed", 0, 0), 0).State);
        Assert.Equal("invalid_report", StructuredTestReportParser.Parse("vitest", Js("mystery", 1, 0), 0).State);
    }

    [Fact]
    public void Trx_solution_bundle_counts_every_report_and_does_not_hide_an_earlier_failure()
    {
        var result = StructuredTestReportParser.Parse("trx", "<TestRuns>" + Trx("Failed", 0, 1) + Trx("Passed", 1, 0) + "</TestRuns>", 0);
        Assert.True(result.ReportParsed); Assert.Equal(2, result.Total); Assert.Equal(1, result.Failed); Assert.Equal("failed", result.State);
    }

    [Fact]
    public void Jest_and_vitest_assertions_have_real_executed_counts()
    {
        foreach (var format in new[] { "jest", "vitest" })
        {
            var result = StructuredTestReportParser.Parse(format, Js("passed", 1, 0), 0);
            Assert.Equal("passed", result.State); Assert.True(result.ReportParsed); Assert.Equal(1, result.Executed);
        }
    }

    [Fact]
    public void Go_repeated_runs_preserve_failure_and_require_finished_test_events()
    {
        const string report = """
        {"Action":"run","Package":"p","Test":"TestA"}
        {"Action":"fail","Package":"p","Test":"TestA"}
        {"Action":"run","Package":"p","Test":"TestA"}
        {"Action":"pass","Package":"p","Test":"TestA"}
        {"Action":"pass","Package":"p"}
        """;
        var result = StructuredTestReportParser.Parse("go-json", report, 0);
        Assert.Equal("failed", result.State); Assert.Equal(2, result.Executed); Assert.Equal(1, result.Failed);
        Assert.Equal("invalid_report", StructuredTestReportParser.Parse("go-json", "{\"Action\":\"run\",\"Test\":\"TestA\"}", 0).State);
        Assert.Equal("invalid_report", StructuredTestReportParser.Parse("go-json", "{\"Action\":\"pass\",\"Test\":\"TestA\"}", 0).State);
    }

    [Fact]
    public void External_xml_entities_and_oversized_reports_are_not_read()
    {
        var xml = "<!DOCTYPE testsuite [<!ENTITY file SYSTEM 'file:///not-a-report'>]><testsuite>&file;</testsuite>";
        Assert.Equal("invalid_report", StructuredTestReportParser.Parse("junit", xml, 0).State);
        Assert.Equal("invalid_report", StructuredTestReportParser.Parse("jest", new string('x', StructuredTestReportParser.MaxReportBytes + 1), 0).State);
    }
}
