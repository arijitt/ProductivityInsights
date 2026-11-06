using ProductivityInsights.Components;
using ProductivityInsights.Metrics;
using ProductivityInsights.Options;
using ProductivityInsights.Services;
using ProductivityInsights.Services.Reports;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddRadzenComponents();
builder.Services.AddScoped<ProductivityInsights.Services.ThemeService>();
builder.Services.AddScoped<ProductivityInsights.Services.Analysis.IAnalysisDataClient, ProductivityInsights.Services.Analysis.AnalysisDataClient>();
builder.Services.AddScoped<ProductivityInsights.Services.Analysis.SelectionAnalysisService>();
builder.Services.AddScoped<ProductivityInsights.Services.Analysis.IIncidentSelectionClient, ProductivityInsights.Services.Analysis.IncidentSelectionClient>();
builder.Services.AddScoped<ProductivityInsights.Services.Analysis.IncidentSelectionService>();
builder.Services.AddOptions<IncidentManagementKustoOptions>()
    .Bind(builder.Configuration.GetSection(IncidentManagementKustoOptions.SectionName))
    .Validate(
        options => Uri.TryCreate(options.ClusterUri, UriKind.Absolute, out _),
        "IncidentManagementKusto:ClusterUri must be an absolute URI.")
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.Database),
        "IncidentManagementKusto:Database is required.")
    .Validate(
        options => options.QueryTimeoutSeconds > 0,
        "IncidentManagementKusto:QueryTimeoutSeconds must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddSingleton<IncidentManagementMetrics>();
builder.Services.AddReportExports(builder.Configuration);
builder.Services.AddHttpClient("FeatureRegistry", client => client.Timeout = TimeSpan.FromMinutes(3));
builder.Services.AddScoped<ProductivityInsights.Services.FeatureRegistry.IFeatureRegistryPullRequestCollector>(provider =>
    new ProductivityInsights.Services.FeatureRegistry.FeatureRegistryPullRequestCollector(
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("FeatureRegistry"),
        async token => (await ProductivityInsights.Utilities.UserToken.GetToken().WaitAsync(token)).Token));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
