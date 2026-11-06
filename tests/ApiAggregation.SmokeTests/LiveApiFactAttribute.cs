using System.Runtime.CompilerServices;

namespace ApiAggregation.SmokeTests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveApiFactAttribute : FactAttribute
{
    public LiveApiFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("RUN_LIVE_SMOKE_TESTS") != "1")
            Skip = "Set RUN_LIVE_SMOKE_TESTS=1 or run scripts/Invoke-SmokeTests.ps1 to call the live APIs.";
    }
}
