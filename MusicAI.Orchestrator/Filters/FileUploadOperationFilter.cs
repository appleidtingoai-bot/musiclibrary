using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Linq;

namespace MusicAI.Orchestrator
{
    /// <summary>
    /// Swagger operation filter to properly display file upload endpoints
    /// Converts IFormFileCollection to file upload controls in Swagger UI
    /// </summary>
    public class FileUploadOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            // Check if this endpoint has IFormFileCollection or IFormFile parameters
            var fileParams = context.MethodInfo.GetParameters()
                .Where(p => p.ParameterType == typeof(Microsoft.AspNetCore.Http.IFormFileCollection) ||
                           p.ParameterType == typeof(Microsoft.AspNetCore.Http.IFormFile))
                .ToList();

            if (fileParams.Count == 0)
                return;

            // Check if Request.Form.Files is accessed in the method
            var methodBody = context.MethodInfo.GetMethodBody();
            var usesFormFiles = context.MethodInfo.Name.Contains("Upload", System.StringComparison.OrdinalIgnoreCase);

            if (usesFormFiles)
            {
                // Clear existing request body if any
                operation.RequestBody = new OpenApiRequestBody
                {
                    Content =
                    {
                        ["multipart/form-data"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = "object",
                                Properties =
                                {
                                    ["files"] = new OpenApiSchema
                                    {
                                        Type = "array",
                                        Items = new OpenApiSchema
                                        {
                                            Type = "string",
                                            Format = "binary"
                                        },
                                        Description = "Upload multiple music files (MP3, WAV, M4A, FLAC)"
                                    }
                                },
                                Required = new System.Collections.Generic.HashSet<string> { "files" }
                            }
                        }
                    },
                    Description = "Upload music files",
                    Required = true
                };
            }
        }
    }
}
