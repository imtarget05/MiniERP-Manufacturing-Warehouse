using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MiniERP.Api.Services;

/// <summary>
/// Publishes the runtime authorization verdict for every operation. Most
/// protected routes carry no <c>RequireAuthorization()</c> metadata: they are
/// guarded by <see cref="MutationAuthorizationMiddleware"/>, which resolves a
/// policy from method plus path. Reading the metadata alone would therefore
/// advertise every mutation as anonymous.
/// </summary>
public sealed class SwaggerAuthOperationFilter : IOperationFilter
{
    public const string SchemeName = "BearerAuth";

    private readonly IReadOnlyDictionary<string, bool> _requiresAuthorization;

    public SwaggerAuthOperationFilter(IEnumerable<EndpointDataSource> dataSources)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in dataSources)
        {
            foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            {
                var template = endpoint.RoutePattern.RawText;
                if (string.IsNullOrEmpty(template)) continue;

                var path = Normalize(template);
                var allowAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
                var hasAuthorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
                var verbs = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
                foreach (var verb in verbs is { Count: > 0 } ? verbs : new List<string> { "GET" })
                {
                    var key = Key(verb, path);
                    var required = !allowAnonymous &&
                        (hasAuthorizeData || MutationAuthorization.PolicyFor(verb, path) is not null);
                    // One route shape can appear in more than one data source; a
                    // protected mapping is never downgraded by a later scan.
                    map[key] = map.TryGetValue(key, out var existing) ? existing || required : required;
                }
            }
        }

        _requiresAuthorization = map;
    }

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var method = context.ApiDescription.HttpMethod ?? "GET";
        var path = Normalize("/" + (context.ApiDescription.RelativePath ?? string.Empty).TrimStart('/'));
        if (!_requiresAuthorization.TryGetValue(Key(method, path), out var required) || !required) return;

        // Idempotent: Swashbuckle builds a fresh operation per document, but
        // appending unconditionally would duplicate the requirement if a filter
        // ever runs twice over the same instance.
        var alreadyRequired = operation.Security?.Any(requirement =>
            requirement.Keys.Any(scheme => scheme.Reference?.Id == SchemeName)) == true;
        if (alreadyRequired) return;

        operation.Security ??= new List<OpenApiSecurityRequirement>();
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = SchemeName }
            }] = new List<string>()
        });
    }

    private static string Key(string method, string path) => $"{method.ToUpperInvariant()} {path}";

    // "{id:long}" in a route template is published as "{id}"; without this the
    // lookup misses every route that constrains a parameter.
    private static string Normalize(string path) =>
        Regex.Replace(path, @"\{(?<name>[A-Za-z0-9_]+)(?::[^}]+)?\}", "{${name}}");
}
