using CSweet.Agent.SDK;
using CSweet.Agents.ChiefOfStaff;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

var manifest = await AgentManifestLoader.LoadAsync("csweet-plugin.json", CancellationToken.None);
if (manifest.Id != ChiefOfStaffProfile.AgentId || manifest.Version != ChiefOfStaffProfile.Version)
    throw new InvalidOperationException("The Chief of Staff implementation identity does not match csweet-plugin.json.");

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    Console.WriteLine($"{manifest.Id} {manifest.Version} self-test passed.");
    return;
}
builder.AddCSweetAgent<ChiefOfStaffAgent>();
builder.Services.AddSingleton<ChiefOfStaffOrchestrator>();

var host = builder.Build();
host.Run();
