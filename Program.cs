using ProductivityInsights.Components;
using ProductivityInsights.Metrics;
using ProductivityInsights.Options;
using ProductivityInsights.Services;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddRadzenComponents();
builder.Services.AddScoped<ProductivityInsights.Services.ThemeService>();
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
