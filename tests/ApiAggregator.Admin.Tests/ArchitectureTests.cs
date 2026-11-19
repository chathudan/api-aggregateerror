using System.Reflection;
using ApiAggregator.Admin.Application;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using Microsoft.AspNetCore.Components;

namespace ApiAggregator.Admin.Tests;

/// <summary>
/// The rule that keeps the admin app a modular monolith (ADR 0014): pages and components go through AdminService,
/// never the store or the engine directly, and the use cases know nothing about HTTP or Blazor.
/// Plain reflection, no extra package.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Type[] Forbidden =
    [
        typeof(IConfigStore), typeof(FileConfigStore), typeof(CompositionExecutor), typeof(DownstreamAuthenticator),
        typeof(ISecretResolver), typeof(OpenApiImporter)
    ];

    [Fact]
    public void Pages_and_components_use_AdminService_never_the_store_or_the_engine()
    {
        var components = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && t.Namespace?.StartsWith("ApiAggregator.Admin.Components") == true)
            .ToList();
        Assert.NotEmpty(components);

        var problems = new List<string>();
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var type in components)
        {
            var used = type.GetFields(all).Select(f => (f.Name, f.FieldType))
                .Concat(type.GetProperties(all).Select(p => (p.Name, p.PropertyType)))
                .Concat(type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => (p.Name ?? "?", p.ParameterType)));
            foreach (var (name, usedType) in used)
                if (Forbidden.Any(f => f.IsAssignableFrom(usedType)))
                    problems.Add($"{type.Name}.{name} is a {usedType.Name}. Pages and components must call AdminService instead.");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void The_use_cases_know_nothing_about_http_endpoints_or_blazor()
    {
        var references = typeof(AdminService).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        var aspNet = references.Where(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)).ToList();
        Assert.True(aspNet.Count == 0,
            $"ApiAggregator.Admin.Application references {string.Join(", ", aspNet)}. Keep HTTP and Blazor in the admin app; pass what the use case needs instead.");
    }
}
