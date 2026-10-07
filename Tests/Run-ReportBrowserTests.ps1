param(
    [string[]] $Fixtures = @('teams-select-all', 'gitCommits', 'git-role-icons', 'workItems', 'incidentManagement', 'incident-active-groups', 'large', 'failed-export', 'slow-export', 'skipped-export', 'picker-stale', 'stale-module', 'incident-available-export', 'incident-unkeyed-export', 'feature-details', 'feature-trends', 'feature-teams', 'feature-empty', 'feature-failed', 'feature-files-failed', 'feature-slow', 'feature-late'),
    [ValidateRange(1024, 65535)] [int] $Port = 5187,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(3)
try {
    if (!$NoBuild) {
        dotnet build Tests\ProductivityInsights.ReportTests.csproj --no-restore --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Report fixture build failed.' }
    }
    $url = "http://127.0.0.1:$Port/"
    foreach ($fixture in $Fixtures) {
        $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('Tests\bin\Debug\net8.0\ProductivityInsights.ReportTests.dll', '--web', '--fixture', $fixture, '--urls', $url)) {
            $start.ArgumentList.Add($argument)
        }
        $process = [System.Diagnostics.Process]::Start($start)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $passed = $false
        try {
            $ready = $false
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while ([DateTime]::UtcNow -lt $deadline -and !$process.HasExited) {
                try {
                    $response = $client.GetAsync("${url}_content/Radzen.Blazor/Radzen.Blazor.js").GetAwaiter().GetResult()
                    $ready = $response.IsSuccessStatusCode
                    $response.Dispose()
                    if ($ready) { break }
                }
                catch [System.Net.Http.HttpRequestException] { }
                catch [System.Threading.Tasks.TaskCanceledException] { }
                Start-Sleep -Milliseconds 200
            }
            if (!$ready) { throw "Fixture $fixture did not become responsive with its static assets." }
            node Tests\reportBrowserTests.mjs $url $fixture
            if ($LASTEXITCODE -ne 0) { throw "Browser fixture $fixture failed." }
            $passed = $true
        }
        finally {
            if (!$process.HasExited) { Stop-Process -Id $process.Id -Force }
            $process.WaitForExit()
            $log = $stdout.GetAwaiter().GetResult()
            $errors = $stderr.GetAwaiter().GetResult()
            if (!$passed) { ($log + $errors) -split "`n" | Select-Object -Last 45 }
            $process.Dispose()
        }
    }
}
finally {
    $client.Dispose()
    Pop-Location
}
