using System.Linq;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MusicAI.AllInOne.Swagger
{
    // Adds an optional `X-Admin-Token` header parameter to endpoints under the Admin controller
    public class RequireAdminHeaderOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            if (operation.Parameters == null)
                operation.Parameters = new System.Collections.Generic.List<OpenApiParameter>();

            // Look for controller route value or relative path containing 'api/admin'
            var controller = context.ApiDescription.ActionDescriptor.RouteValues.TryGetValue("controller", out var c) ? c : null;
            var path = context.ApiDescription.RelativePath ?? string.Empty;

            if (string.Equals(controller, "Admin", System.StringComparison.OrdinalIgnoreCase) || path.IndexOf("api/admin", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // If the header is not already present, add it
                if (!operation.Parameters.Any(p => p.Name == "X-Admin-Token"))
                {
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "X-Admin-Token",
                        In = ParameterLocation.Header,
                        Required = false,
                        Description = "Opaque admin session token (paste value from superadmin registration or admin login).",
                        Schema = new OpenApiSchema { Type = "string" }
                    });
                }
            }
        }
    }
}
