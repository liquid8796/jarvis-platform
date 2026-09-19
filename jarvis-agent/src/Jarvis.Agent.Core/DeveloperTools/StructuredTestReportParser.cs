using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Jarvis.Agent.Core.DeveloperTools;

/// <summary>Parses measured test reports. No exit-zero or arbitrary console text is a substitute for executed tests.</summary>
public static class StructuredTestReportParser
{
    public const int MaxReportBytes = 8 * 1024 * 1024;
    private sealed record Counts(int Passed, int Failed, int Skipped, bool FrameworkFailed = false)
    {
        public int Total => checked(Passed + Failed + Skipped);
    }

    public static StructuredTestSummary Parse(string format, string content, int exitCode, string output = "", int maxOutputChars = 64000)
    {
        if (maxOutputChars < 1) throw new ArgumentOutOfRangeException(nameof(maxOutputChars));
        format = (format ?? "").Trim().ToLowerInvariant();
        var diagnostics = new List<string>();
        Counts counts = new(0, 0, 0);
        var parsed = false;
        var state = "unrecognized";
        try
        {
            if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("No test report was produced.");
            if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxReportBytes) throw new InvalidDataException("Test report exceeds the bounded report size.");
            counts = format switch
            {
                "trx" => ParseTrx(content),
                "junit" => ParseJunit(content),
                "jest" or "vitest" => ParseJavaScript(content),
                "go-json" => ParseGo(content),
                _ => throw new NotSupportedException("Unsupported test report format: " + format)
            };
            parsed = true;
            state = counts.Failed > 0 || counts.FrameworkFailed ? "failed" : counts.Passed + counts.Failed == 0 ? "not_run" : "passed";
            if (state == "not_run") diagnostics.Add("The report contains no executed tests; zero-test and skipped-only runs do not pass.");
            if (counts.FrameworkFailed) diagnostics.Add("The test framework reported a suite/build/interruption failure.");
        }
        catch (NotSupportedException ex) { diagnostics.Add(ex.Message); }
        catch (Exception ex) when (ex is JsonException or XmlException or InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        {
            state = "invalid_report";
            diagnostics.Add(ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message);
        }
        if (exitCode != 0) { state = "failed"; diagnostics.Add("Test process exited with code " + exitCode + "."); }
        output ??= "";
        return new(exitCode, counts.Passed, counts.Failed, counts.Skipped, counts.Total,
            output.Length > maxOutputChars ? output[^maxOutputChars..] : output, output.Length > maxOutputChars)
        {
            State = state, Executed = counts.Passed + counts.Failed, ReportParsed = parsed,
            ReportFormat = format, Diagnostics = diagnostics
        };
    }

    private static XDocument Xml(string content)
    {
        using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxReportBytes });
        return XDocument.Load(reader);
    }

    private static Counts ParseTrx(string content)
    {
        var xml = Xml(content);
        if (xml.Root?.Name.LocalName == "TestRuns")
        {
            var runs = xml.Root.Elements().ToArray();
            if (runs.Length == 0 || runs.Any(run => run.Name.LocalName != "TestRun")) throw new InvalidDataException("Invalid TRX report bundle.");
            var parsedRuns = runs.Select(run => ParseTrx(run.ToString(SaveOptions.DisableFormatting))).ToArray();
            return new(parsedRuns.Sum(r => r.Passed), parsedRuns.Sum(r => r.Failed), parsedRuns.Sum(r => r.Skipped), parsedRuns.Any(r => r.FrameworkFailed));
        }
        if (xml.Root?.Name.LocalName != "TestRun") throw new InvalidDataException("Expected a TRX TestRun root.");
        var results = xml.Descendants().Where(e => e.Name.LocalName == "UnitTestResult" && !e.Descendants().Any(c => c.Name.LocalName == "UnitTestResult")).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var passed = 0; var failed = 0; var skipped = 0;
        foreach (var result in results)
        {
            var identity = (string?)result.Attribute("executionId") ?? ((string?)result.Attribute("testId") + "|" + (string?)result.Attribute("testName"));
            if (identity != "|" && !seen.Add(identity)) throw new InvalidDataException("TRX contains duplicate test execution identities.");
            switch ((string?)result.Attribute("outcome"))
            {
                case "Passed": passed++; break;
                case "Failed": case "Error": case "Timeout": case "Aborted": failed++; break;
                case "NotExecuted": case "NotRunnable": case "Inconclusive": case "Skipped": skipped++; break;
                default: throw new InvalidDataException("TRX contains an unknown or missing test outcome.");
            }
        }
        var counts = new Counts(passed, failed, skipped);
        var counters = xml.Descendants().SingleOrDefault(e => e.Name.LocalName == "Counters");
        if (counters is not null)
        {
            Match(counters, "total", counts.Total); Match(counters, "passed", passed);
            var declaredFailed = new[] { "failed", "error", "timeout", "aborted" }.Sum(name => Attribute(counters, name) ?? 0);
            if (declaredFailed != failed) throw new InvalidDataException("TRX failure counters disagree with test outcomes.");
            if (Attribute(counters, "executed") is { } executed && (executed < passed + failed || executed > counts.Total))
                throw new InvalidDataException("TRX executed counter is inconsistent.");
        }
        var summary = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "ResultSummary");
        return counts with { FrameworkFailed = (string?)summary?.Attribute("outcome") is "Failed" or "Error" or "Aborted" or "Timeout" };
    }

    private static Counts ParseJunit(string content)
    {
        var xml = Xml(content);
        if (xml.Root?.Name.LocalName is not ("testsuites" or "testsuite")) throw new InvalidDataException("Expected a JUnit testsuite/testsuites root.");
        Counts Count(XElement root)
        {
            var cases = root.Descendants().Where(e => e.Name.LocalName == "testcase").ToArray();
            var failures = cases.Count(e => e.Elements().Any(c => c.Name.LocalName is "failure" or "error"));
            var skips = cases.Count(e => !e.Elements().Any(c => c.Name.LocalName is "failure" or "error") &&
                (e.Elements().Any(c => c.Name.LocalName == "skipped") || (string?)e.Attribute("status") is "notrun" or "disabled" or "skipped"));
            return new(cases.Length - failures - skips, failures, skips);
        }
        foreach (var suite in xml.Root.DescendantsAndSelf().Where(e => e.Name.LocalName is "testsuite" or "testsuites"))
        {
            var count = Count(suite);
            Match(suite, "tests", count.Total);
            if (suite.Attribute("failures") is not null || suite.Attribute("errors") is not null)
                if ((Attribute(suite, "failures") ?? 0) + (Attribute(suite, "errors") ?? 0) != count.Failed)
                    throw new InvalidDataException("JUnit failure counters disagree with testcase results.");
            if (suite.Attribute("skipped") is not null || suite.Attribute("disabled") is not null)
                if ((Attribute(suite, "skipped") ?? 0) + (Attribute(suite, "disabled") ?? 0) != count.Skipped)
                    throw new InvalidDataException("JUnit skipped/disabled counters disagree with testcase results.");
        }
        return Count(xml.Root);
    }

    private static Counts ParseJavaScript(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("testResults", out var suites) || suites.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Expected Jest/Vitest JSON testResults.");
        foreach (var name in new[] { "numTotalTests", "numPassedTests", "numFailedTests" })
            if (!root.TryGetProperty(name, out _)) throw new InvalidDataException("Jest/Vitest report is missing " + name + ".");
        var passed = 0; var failed = 0; var skipped = 0; var pending = 0; var todo = 0; var frameworkFailed = false;
        foreach (var suite in suites.EnumerateArray())
        {
            if (suite.TryGetProperty("status", out var suiteStatus) && suiteStatus.ValueKind == JsonValueKind.String && suiteStatus.GetString() == "failed") frameworkFailed = true;
            if (suite.TryGetProperty("testExecError", out var executionError) && executionError.ValueKind != JsonValueKind.Null) frameworkFailed = true;
            if (!suite.TryGetProperty("assertionResults", out var assertions) || assertions.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Jest/Vitest suite is missing assertionResults.");
            foreach (var assertion in assertions.EnumerateArray())
                switch (assertion.TryGetProperty("status", out var status) ? status.GetString() : null)
                {
                    case "passed": passed++; break;
                    case "failed": failed++; break;
                    case "todo": todo++; skipped++; break;
                    case "pending": case "skipped": case "disabled": pending++; skipped++; break;
                    default: throw new InvalidDataException("Jest/Vitest contains an unknown test status.");
                }
        }
        var result = new Counts(passed, failed, skipped);
        Match(root, "numTotalTests", result.Total); Match(root, "numPassedTests", passed); Match(root, "numFailedTests", failed);
        Match(root, "numPendingTests", pending); Match(root, "numTodoTests", todo);
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) frameworkFailed = true;
        if (root.TryGetProperty("wasInterrupted", out var interrupted) && interrupted.ValueKind == JsonValueKind.True) frameworkFailed = true;
        if (root.TryGetProperty("numFailedTestSuites", out var failedSuites) && (!failedSuites.TryGetInt32(out var number) || number < 0))
            throw new InvalidDataException("Invalid failed suite count.");
        else if (failedSuites.ValueKind == JsonValueKind.Number && failedSuites.GetInt32() > 0) frameworkFailed = true;
        return result with { FrameworkFailed = frameworkFailed };
    }

    private static Counts ParseGo(string content)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        var terminal = new HashSet<string>(StringComparer.Ordinal);
        var passed = 0; var failed = 0; var skipped = 0; var frameworkFailed = false; var events = 0;
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Action", out var actionNode) || actionNode.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Go JSON line is missing Action.");
            events++;
            var action = actionNode.GetString();
            var test = root.TryGetProperty("Test", out var testNode) && testNode.ValueKind == JsonValueKind.String ? testNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(test)) { if (action is "fail" or "build-fail") frameworkFailed = true; continue; }
            var package = root.TryGetProperty("Package", out var packageNode) ? packageNode.GetString() : "";
            var id = package + "|" + test;
            if (action == "run") { if (!active.Add(id)) throw new InvalidDataException("Duplicate active Go test."); terminal.Remove(id); }
            else if (action is "pass" or "fail" or "skip")
            {
                if (!active.Contains(id)) throw new InvalidDataException("Go terminal test outcome has no corresponding run event.");
                if (!terminal.Add(id)) throw new InvalidDataException("Duplicate terminal Go test outcome without a new run.");
                active.Remove(id);
                if (action == "pass") passed++; else if (action == "fail") failed++; else skipped++;
            }
        }
        if (events == 0 || active.Count > 0) throw new InvalidDataException("Go test report is empty or has unfinished tests.");
        return new(passed, failed, skipped, frameworkFailed);
    }

    private static int? Attribute(XElement element, string name)
    {
        if (element.Attribute(name) is not { } value) return null;
        return int.TryParse(value.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number : throw new InvalidDataException("Invalid test counter: " + name);
    }
    private static void Match(XElement element, string name, int actual)
    {
        if (Attribute(element, name) is { } expected && expected != actual) throw new InvalidDataException("Report counter " + name + " disagrees with test outcomes.");
    }
    private static void Match(JsonElement element, string name, int actual)
    {
        if (element.TryGetProperty(name, out var value) && (!value.TryGetInt32(out var expected) || expected < 0 || expected != actual))
            throw new InvalidDataException("Report counter " + name + " disagrees with test outcomes.");
    }
}
