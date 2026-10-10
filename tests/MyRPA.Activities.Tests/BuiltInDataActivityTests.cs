using Microsoft.Extensions.DependencyInjection;
using MyRPA.Core.Execution;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;
using MyRPA.Workflow.Values;

namespace MyRPA.Activities.Tests;

/// <summary>The Phase 7 built-ins (ADR-0042): text, JSON, date and collection activities through the real engine.</summary>
public sealed class BuiltInDataActivityTests
{
    private const string Out = """[ { "name": "out", "direction": "Out", "type": "Object" }, { "name": "index", "direction": "Out", "type": "Int" } ]""";

    private const string Orders = "[{ id: 1, amount: 120, status: 'open' }, { id: 2, amount: 40, status: 'paid' }, { id: 3, amount: 300, status: 'open' }, { id: 4, amount: null, status: 'open' }]";

    /// <summary>Runs one activity whose result goes to the Out argument 'out'.</summary>
    private static async Task<WorkflowExecutionResult> RunAsync(string type, string properties)
    {
        using var h = new ActivityHarness();
        var all = properties.Length == 0 ? "\"result\": \"out\"" : $"{properties}, \"result\": \"out\"";
        return await h.RunAsync(ActivityHarness.Workflow($$"""{ "id": "a", "type": "{{type}}", "properties": { {{all}} } }""", Out));
    }

    private static async Task<object?> ValueAsync(string type, string properties)
    {
        var result = await RunAsync(type, properties);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Outputs["out"];
    }

    private static async Task<ExecutionError> FailureAsync(string type, string properties)
    {
        var result = await RunAsync(type, properties);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        return result.Error!;
    }

    private static string Json(object? value) => WorkflowValues.ToJsonString(value);

    private static List<object?> Ids(object? rows) => [.. ((IReadOnlyList<object?>)rows!).Select(o => ((IReadOnlyDictionary<string, object?>)o!)["id"])];

    [Fact]
    public async Task TextSplit_SplitsAtTheSeparator_TrimmingAndDroppingEmptyParts()
    {
        Assert.Equal("""["a"," b","","c"]""", Json(await ValueAsync("Core.Text.Split", """ "text": "'a, b,,c'", "separator": "','" """)));
        Assert.Equal("""["a","b","c"]""", Json(await ValueAsync("Core.Text.Split", """ "text": "'a, b,,c'", "separator": "','", "trim": true, "removeEmpty": true """)));
        Assert.Equal("InvalidInput", (await FailureAsync("Core.Text.Split", """ "text": "'abc'", "separator": "''" """)).ErrorType);
    }

    [Fact]
    public async Task TextJoin_WritesItemsAsTheyDisplay()
    {
        Assert.Equal("1 | two | true", await ValueAsync("Core.Text.Join", """ "items": "[1, 'two', true]", "separator": "' | '" """));
        Assert.Equal("ab", await ValueAsync("Core.Text.Join", """ "items": "['a', 'b']" """));
    }

    [Fact]
    public async Task TextMatch_ReturnsTheMatchWithItsGroups_OrNull_OrEveryMatch()
    {
        Assert.Equal(
            """{"value":"INV-2026-0042","index":6,"groups":{"year":"2026","number":"0042"}}""",
            Json(await ValueAsync("Core.Text.Match", """ "text": "'Order INV-2026-0042 paid'", "pattern": "'INV-(?<year>\\\\d{4})-(?<number>\\\\d+)'" """)));
        Assert.Null(await ValueAsync("Core.Text.Match", """ "text": "'nothing here'", "pattern": "'INV-\\\\d+'" """));
        var all = (IReadOnlyList<object?>)(await ValueAsync("Core.Text.Match", """ "text": "'a1 b22 C3'", "pattern": "'[a-z]\\\\d+'", "all": true """))!;
        Assert.Equal(["a1", "b22"], all.Select(m => ((IReadOnlyDictionary<string, object?>)m!)["value"]));
        Assert.Equal(3, ((IReadOnlyList<object?>)(await ValueAsync("Core.Text.Match", """ "text": "'a1 b22 C3'", "pattern": "'[a-z]\\\\d+'", "all": true, "ignoreCase": true """))!).Count);
    }

    [Fact]
    public async Task TextMatch_WithARawStringPattern_NeedsNoDoubledBackslashes()
    {
        // ADR-0043: r'...' keeps the pattern as written; only JSON's own escaping remains (\\ in the file is \).
        Assert.Equal(
            "INV-2026-0042",
            ((IReadOnlyDictionary<string, object?>)(await ValueAsync("Core.Text.Match", """ "text": "'Order INV-2026-0042 paid'", "pattern": "r'INV-\\d{4}-\\d+'" """))!)["value"]);
    }

    [Fact]
    public async Task TextMatch_RefusesABadPattern_AndStopsACatastrophicOne()
    {
        Assert.Equal("InvalidPattern", (await FailureAsync("Core.Text.Match", """ "text": "'x'", "pattern": "'(unclosed'" """)).ErrorType);
        Assert.Equal("Timeout", (await FailureAsync("Core.Text.Match", """ "text": "'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!'", "pattern": "'^(a+)+$'" """)).ErrorType);
    }

    [Fact]
    public async Task TextReplace_ReplacesMatches_WithGroups()
    {
        Assert.Equal("2026-12-31", await ValueAsync("Core.Text.Replace", """ "text": "'31/12/2026'", "pattern": "'(\\\\d+)/(\\\\d+)/(\\\\d+)'", "replacement": "'$3-$2-$1'" """));
    }

    [Fact]
    public async Task JsonParse_ReadsObjectsAndLists_AndRefusesBadJson_WithoutRepeatingIt()
    {
        Assert.Equal("""{"id":7,"tags":["a","b"],"ok":true}""", Json(await ValueAsync("Core.Json.Parse", """ "text": "'{\"id\": 7, \"tags\": [\"a\", \"b\"], \"ok\": true}'" """)));

        var bad = await FailureAsync("Core.Json.Parse", """ "text": "'{\"password\": \"hunter2\"'" """);
        Assert.Equal("InvalidJson", bad.ErrorType);
        Assert.DoesNotContain("hunter2", bad.Message, StringComparison.Ordinal);

        var deep = string.Concat(Enumerable.Repeat("[", 70)) + string.Concat(Enumerable.Repeat("]", 70));
        Assert.Equal("InvalidJson", (await FailureAsync("Core.Json.Parse", $$""" "text": "'{{deep}}'" """)).ErrorType);
    }

    [Fact]
    public async Task JsonSerialize_WritesJson_OptionallyIndented()
    {
        Assert.Equal("""{"a":1,"b":[true,null]}""", await ValueAsync("Core.Json.Serialize", """ "value": "{ a: 1, b: [true, null] }" """));
        Assert.Contains("\n", (string)(await ValueAsync("Core.Json.Serialize", """ "value": "{ a: 1 }", "indented": true """))!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DateParse_ReadsAnExactFormat_InATimeZone_OrAnOffsetGivenInTheText()
    {
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 10, 30, 0, TimeSpan.FromHours(1)), await ValueAsync("Core.Date.Parse", """ "text": "'31/12/2026 10:30'", "format": "'dd/MM/yyyy HH:mm'", "timeZone": "'Europe/Paris'" """));
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 10, 30, 0, TimeSpan.FromHours(2)), await ValueAsync("Core.Date.Parse", """ "text": "'01/07/2026 10:30'", "format": "'dd/MM/yyyy HH:mm'", "timeZone": "'Europe/Paris'" """));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5)), await ValueAsync("Core.Date.Parse", """ "text": "'2026-01-02T03:04:05-05:00'" """));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), await ValueAsync("Core.Date.Parse", """ "text": "'2026-01-02'" """));
    }

    [Fact]
    public async Task DateParse_SaysWhatIsWrong_ForAMismatch_AnUnknownZone_AndANonExistentTime()
    {
        Assert.Equal("InvalidFormat", (await FailureAsync("Core.Date.Parse", """ "text": "'2026-12-31'", "format": "'dd/MM/yyyy'" """)).ErrorType);
        Assert.Equal("InvalidTimeZone", (await FailureAsync("Core.Date.Parse", """ "text": "'2026-12-31'", "timeZone": "'Mars/Olympus'" """)).ErrorType);
        // 29 March 2026, 02:30 does not exist in Paris (clocks go from 02:00 to 03:00).
        Assert.Equal("InvalidInput", (await FailureAsync("Core.Date.Parse", """ "text": "'2026-03-29 02:30'", "format": "'yyyy-MM-dd HH:mm'", "timeZone": "'Europe/Paris'" """)).ErrorType);
    }

    [Fact]
    public async Task DateFormat_WritesInAFormatAndZone()
    {
        Assert.Equal("31 Dec 2026 11:30", await ValueAsync("Core.Date.Format", """ "value": "toDateTime('2026-12-31T10:30:00Z')", "format": "'dd MMM yyyy HH:mm'", "timeZone": "'Europe/Paris'" """));
        Assert.Equal("2026-12-31T10:30:00+00:00", await ValueAsync("Core.Date.Format", """ "value": "toDateTime('2026-12-31T10:30:00Z')" """));
    }

    [Fact]
    public async Task DateAdd_AndDifference_Calculate()
    {
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 12, 0, 0, TimeSpan.Zero), await ValueAsync("Core.Date.Add", """ "value": "toDateTime('2026-01-31T10:00:00Z')", "months": 1, "hours": 2 """));
        Assert.Equal(new DateTimeOffset(2026, 1, 30, 10, 0, 0, TimeSpan.Zero), await ValueAsync("Core.Date.Add", """ "value": "toDateTime('2026-01-31T10:00:00Z')", "days": -1 """));
        Assert.Equal(36m, await ValueAsync("Core.Date.Difference", """ "from": "toDateTime('2026-01-01T00:00:00Z')", "to": "toDateTime('2026-01-02T12:00:00Z')", "unit": "Hours" """));
        Assert.Equal(-1.5m, await ValueAsync("Core.Date.Difference", """ "from": "toDateTime('2026-01-02T12:00:00Z')", "to": "toDateTime('2026-01-01T00:00:00Z')" """));
    }

    [Fact]
    public void ChoiceProperties_AreValidatedBeforeRunning()
    {
        using var h = new ActivityHarness();
        var result = h.Services.GetRequiredService<WorkflowLoader>().Load(ActivityHarness.Workflow(
            """{ "id": "d", "type": "Core.Date.Difference", "properties": { "from": "now()", "to": "now()", "unit": "Weeks", "result": "out" } }""", Out));

        Assert.Equal([DiagnosticCodes.ValueNotAllowed], result.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task CollectionSort_SortsValuesOrATableColumn_NullFirst_AndRefusesMixedKinds()
    {
        Assert.Equal("[3,2,1]", Json(await ValueAsync("Core.Collection.Sort", """ "items": "[2, 3, 1]", "descending": true """)));
        Assert.Equal([4L, 2L, 1L, 3L], Ids(await ValueAsync("Core.Collection.Sort", $$""" "items": "{{Orders}}", "key": "'amount'" """)));
        Assert.Equal("InvalidInput", (await FailureAsync("Core.Collection.Sort", """ "items": "[1, 'a']" """)).ErrorType);
    }

    [Fact]
    public async Task CollectionFilter_KeepsRowsThatMeetTheCondition()
    {
        Assert.Equal([1L, 3L], Ids(await ValueAsync("Core.Collection.Filter", $$""" "items": "{{Orders}}", "key": "'amount'", "operator": "GreaterThan", "value": "100" """)));
        Assert.Equal([1L, 3L, 4L], Ids(await ValueAsync("Core.Collection.Filter", $$""" "items": "{{Orders}}", "key": "'status'", "value": "'open'" """)));
        Assert.Equal([4L], Ids(await ValueAsync("Core.Collection.Filter", $$""" "items": "{{Orders}}", "key": "'amount'", "operator": "IsNull" """)));
        Assert.Equal("[\"apple pie\"]", Json(await ValueAsync("Core.Collection.Filter", """ "items": "['apple pie', 'pear']", "operator": "Contains", "value": "'apple'" """)));
    }

    [Fact]
    public async Task CollectionFind_GivesTheFirstMatchAndItsIndex_OrNullAndMinusOne()
    {
        using var h = new ActivityHarness();
        var found = await h.RunAsync(ActivityHarness.Workflow(
            $$"""{ "id": "f", "type": "Core.Collection.Find", "properties": { "items": "{{Orders}}", "key": "'status'", "value": "'paid'", "result": "out", "index": "index" } }""", Out));
        var missing = await h.RunAsync(ActivityHarness.Workflow(
            $$"""{ "id": "f", "type": "Core.Collection.Find", "properties": { "items": "{{Orders}}", "key": "'status'", "value": "'void'", "result": "out", "index": "index" } }""", Out));

        Assert.Equal((2L, 1L), (((IReadOnlyDictionary<string, object?>)found.Outputs["out"]!)["id"], found.Outputs["index"]));
        Assert.Equal((null, -1L), (missing.Outputs["out"], missing.Outputs["index"]));
    }

    [Fact]
    public async Task CollectionMerge_ConcatenatesLists_OptionallyWithoutRepeats()
    {
        Assert.Equal("[1,2,2,3]", Json(await ValueAsync("Core.Collection.Merge", """ "first": "[1, 2]", "second": "[2, 3]" """)));
        Assert.Equal("[1,2,3]", Json(await ValueAsync("Core.Collection.Merge", """ "first": "[1, 2]", "second": "[2, 3]", "distinct": true """)));
    }

    [Fact]
    public async Task WrongInputKinds_FailWithInvalidInput_NamingTheProperty()
    {
        var error = await FailureAsync("Core.Collection.Sort", """ "items": "'not a list'" """);

        Assert.Equal("InvalidInput", error.ErrorType);
        Assert.Contains("'items'", error.Message, StringComparison.Ordinal);
    }
}
