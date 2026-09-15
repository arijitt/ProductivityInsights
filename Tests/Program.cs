using System.Reflection;
using ProductivityInsights.ReportTests;

if (args.Contains("--web"))
{
    await BrowserFixtureHost.RunAsync(args);
    return;
}
if (args.Contains("--live"))
{
    await LiveReportProbe.RunAsync(args);
    return;
}
if (args.Contains("--live-incident"))
{
    await LiveIncidentReportProbe.RunAsync(args);
    return;
}

var fixtures = Assembly.GetExecutingAssembly().GetTypes()
    .OrderBy(type => type.FullName)
    .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.Name is "Run" or "RunAsync" && method.GetParameters().Length == 0))
    .ToArray();
if (fixtures.Length == 0)
    throw new InvalidOperationException("No report fixtures were found.");

foreach (var fixture in fixtures)
{
    try
    {
        if (fixture.Invoke(null, null) is Task task)
            await task;
        Console.WriteLine($"PASS {fixture.DeclaringType!.Name}");
    }
    catch (TargetInvocationException exception) when (exception.InnerException is not null)
    {
        throw new InvalidOperationException($"FAIL {fixture.DeclaringType!.Name}", exception.InnerException);
    }
}
Console.WriteLine($"{fixtures.Length} report fixture groups passed.");
